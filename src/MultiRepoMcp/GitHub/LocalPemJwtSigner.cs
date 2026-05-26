using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MultiRepoMcp.Configuration;

namespace MultiRepoMcp.GitHub;

/// <summary>
/// Local-file PEM-backed <see cref="IJwtSigner"/>. Loads the PEM lazily on
/// first sign, parses it into an <see cref="RSA"/>, and signs in-process.
/// Intended for development only — the production path is
/// <see cref="KeyVaultJwtSigner"/>, which never exposes the key material to
/// this process.
/// </summary>
internal sealed class LocalPemJwtSigner : IJwtSigner, IDisposable
{
    private readonly string _pemPath;
    private readonly ILogger<LocalPemJwtSigner> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RSA? _rsa;

    public LocalPemJwtSigner(
        IOptions<GitHubAppOptions> options,
        ILogger<LocalPemJwtSigner> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        var path = options.Value.LocalPrivateKeyPath;
        if (string.IsNullOrEmpty(path))
        {
            throw new InvalidOperationException(
                $"{nameof(GitHubAppOptions)}.{nameof(GitHubAppOptions.LocalPrivateKeyPath)} is required " +
                "to construct a LocalPemJwtSigner.");
        }
        _pemPath = path;
        _logger = logger;
    }

    public async ValueTask<byte[]> SignDigestAsync(byte[] digest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(digest);
        var rsa = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        // PKCS#1 v1.5 over the supplied SHA-256 digest — same wire format
        // GitHub's verifier expects for the JWT "RS256" alg.
        return rsa.SignHash(digest, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    private async ValueTask<RSA> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_rsa is not null)
        {
            return _rsa;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_rsa is not null)
            {
                return _rsa;
            }

            _logger.LogWarning(
                "Loading GitHub App private key from local file {Path}. Do not use in production.",
                _pemPath);

            var pem = await File.ReadAllTextAsync(_pemPath, cancellationToken).ConfigureAwait(false);
            var rsa = RSA.Create();
            try
            {
                rsa.ImportFromPem(pem);
            }
            catch (Exception ex)
            {
                rsa.Dispose();
                throw new InvalidOperationException(
                    $"GitHub App private key at '{_pemPath}' is not a valid PEM-encoded RSA key.", ex);
            }

            _rsa = rsa;
            return _rsa;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Dispose();
        _rsa?.Dispose();
    }
}
