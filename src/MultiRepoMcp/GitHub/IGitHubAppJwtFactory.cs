namespace MultiRepoMcp.GitHub;

/// <summary>
/// Mints short-lived (≤ 10 min) RS256 JWTs that authenticate API calls as
/// the GitHub App itself (used by installation discovery and the GitHub
/// health check).
/// </summary>
public interface IGitHubAppJwtFactory
{
    ValueTask<string> CreateAsync(CancellationToken cancellationToken);
}
