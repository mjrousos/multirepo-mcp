using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Keys.Cryptography;
using Microsoft.Extensions.Options;
using MultiRepoMcp.Configuration;

namespace MultiRepoMcp.GitHub;

/// <summary>
/// <see cref="IJwtSigner"/> that performs the RS256 signing step inside
/// Azure Key Vault via the <c>/sign</c> REST API. The private key never
/// leaves the vault — this process only ever sees the signature output.
///
/// The bound principal needs the <c>Sign</c> key permission (RBAC role
/// <c>Key Vault Crypto User</c> is sufficient). If the principal also has
/// <c>Get</c> on the key the Azure SDK will *attempt* local cryptography
/// for performance; we do not rely on that path and recommend granting only
/// <c>Sign</c> so the key material stays in the vault.
/// </summary>
internal sealed class KeyVaultJwtSigner : IJwtSigner
{
    // Delegate seam: production wraps a real CryptographyClient.SignAsync
    // call; tests inject a stub. Using a delegate (instead of mocking
    // CryptographyClient directly) avoids the SignResult construction
    // friction — SignResult has only internal constructors and no public
    // model factory.
    private readonly Func<byte[], CancellationToken, ValueTask<byte[]>> _signOperation;
    private readonly ILogger<KeyVaultJwtSigner> _logger;

    public KeyVaultJwtSigner(
        IOptions<GitHubAppOptions> options,
        ILogger<KeyVaultJwtSigner> logger)
        : this(options, logger, new DefaultAzureCredential(), signOperationForTests: null)
    {
    }

    internal KeyVaultJwtSigner(
        IOptions<GitHubAppOptions> options,
        ILogger<KeyVaultJwtSigner> logger,
        TokenCredential credential,
        Func<byte[], CancellationToken, ValueTask<byte[]>>? signOperationForTests)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credential);
        _logger = logger;

        if (signOperationForTests is not null)
        {
            _signOperation = signOperationForTests;
            return;
        }

        var opts = options.Value;
        if (opts.KeyVaultUri is null)
        {
            throw new InvalidOperationException(
                $"{nameof(GitHubAppOptions)}.{nameof(GitHubAppOptions.KeyVaultUri)} is required for KeyVaultJwtSigner.");
        }
        if (string.IsNullOrEmpty(opts.PrivateKeyName))
        {
            throw new InvalidOperationException(
                $"{nameof(GitHubAppOptions)}.{nameof(GitHubAppOptions.PrivateKeyName)} is required for KeyVaultJwtSigner.");
        }

        // Key identifier URI of the form
        //   https://{vault}.vault.azure.net/keys/{name}
        // Omitting the version selects the latest enabled version, which is
        // what we want for transparent rotation via Key Vault.
        var keyIdentifier = new Uri(opts.KeyVaultUri, $"keys/{opts.PrivateKeyName}");
        var cryptoClient = new CryptographyClient(keyIdentifier, credential);
        _signOperation = async (digest, ct) =>
        {
            var result = await cryptoClient
                .SignAsync(SignatureAlgorithm.RS256, digest, ct)
                .ConfigureAwait(false);
            return result.Signature;
        };
    }

    public async ValueTask<byte[]> SignDigestAsync(byte[] digest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(digest);
        try
        {
            return await _signOperation(digest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Key Vault RS256 sign operation failed.");
            throw new InvalidOperationException(
                "Failed to sign GitHub App JWT via Key Vault.", ex);
        }
    }
}
