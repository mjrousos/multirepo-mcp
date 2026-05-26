using System.Security.Cryptography;

namespace MultiRepoMcp.GitHub;

/// <summary>
/// Returns the GitHub App's RSA private key. Implementations are responsible
/// for caching: the key is loaded once at startup.
/// </summary>
public interface IGitHubPrivateKeyProvider
{
    ValueTask<RSA> GetPrivateKeyAsync(CancellationToken cancellationToken);
}
