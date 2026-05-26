using System.Security.Cryptography;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MultiRepoMcp.Configuration;

namespace MultiRepoMcp.GitHub;

/// <summary>
/// Loads the GitHub App's PEM private key from Azure Key Vault (via
/// <see cref="DefaultAzureCredential"/> / Managed Identity) the first time it
/// is requested, then caches the parsed <see cref="RSA"/> instance for the
/// life of the process.
/// </summary>
internal sealed class KeyVaultPrivateKeyProvider : IGitHubPrivateKeyProvider, IDisposable
{
    private readonly SecretClient _secretClient;
    private readonly GitHubAppOptions _options;
    private readonly ILogger<KeyVaultPrivateKeyProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RSA? _cachedKey;

    public KeyVaultPrivateKeyProvider(
        IOptions<GitHubAppOptions> options,
        ILogger<KeyVaultPrivateKeyProvider> logger,
        SecretClient? secretClientForTests = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger;

        if (secretClientForTests is not null)
        {
            _secretClient = secretClientForTests;
        }
        else
        {
            if (_options.KeyVaultUri is null)
            {
                throw new InvalidOperationException(
                    $"{nameof(GitHubAppOptions)}.{nameof(GitHubAppOptions.KeyVaultUri)} is required " +
                    "when no SecretClient is injected (tests can inject one).");
            }

            _secretClient = new SecretClient(_options.KeyVaultUri, new DefaultAzureCredential());
        }
    }

    public async ValueTask<RSA> GetPrivateKeyAsync(CancellationToken cancellationToken)
    {
        if (_cachedKey is not null)
        {
            return _cachedKey;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedKey is not null)
            {
                return _cachedKey;
            }

            // Optional dev override: load PEM from a local file. Documented as
            // dev-only in the README; in production this is unset and the
            // Key Vault path is taken.
            string pem;
            if (!string.IsNullOrEmpty(_options.LocalPrivateKeyPath))
            {
                _logger.LogWarning(
                    "Loading GitHub App private key from local file {Path}. Do not use in production.",
                    _options.LocalPrivateKeyPath);
                pem = await File.ReadAllTextAsync(_options.LocalPrivateKeyPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                _logger.LogInformation(
                    "Loading GitHub App private key from Key Vault secret {SecretName}.",
                    _options.PrivateKeySecretName);

                KeyVaultSecret secret;
                try
                {
                    var response = await _secretClient.GetSecretAsync(
                            _options.PrivateKeySecretName,
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    secret = response.Value;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to load GitHub App private key from Key Vault.");
                    throw new InvalidOperationException(
                        "Failed to load GitHub App private key from Key Vault.", ex);
                }

                pem = secret.Value;
            }

            var rsa = RSA.Create();
            try
            {
                rsa.ImportFromPem(pem);
            }
            catch (Exception ex)
            {
                rsa.Dispose();
                _logger.LogError(ex, "GitHub App private key secret is not a valid PEM-encoded RSA key.");
                throw new InvalidOperationException(
                    "GitHub App private key secret is not a valid PEM-encoded RSA key.", ex);
            }

            _cachedKey = rsa;
            return _cachedKey;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        _cachedKey?.Dispose();
    }
}
