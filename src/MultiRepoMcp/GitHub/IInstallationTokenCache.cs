namespace MultiRepoMcp.GitHub;

public readonly record struct CachedToken(string Token, DateTimeOffset ExpiresAtUtc)
{
    public TimeSpan RemainingLifetime(DateTimeOffset now) =>
        ExpiresAtUtc > now ? ExpiresAtUtc - now : TimeSpan.Zero;
}

/// <summary>
/// Per-(installation, repo) installation access token (IAT) cache with
/// single-flight refresh and proactive refresh ahead of expiry.
///
/// Tokens are minted with <c>repositories: [repo]</c> so each cached IAT can
/// only access the named target repo — this is the primary defense for the
/// search_code "query-escape" risk: a maliciously-crafted query cannot reach
/// other repos in the same installation because the token itself is scoped
/// down at GitHub's authoritative boundary.
/// </summary>
public interface IInstallationTokenCache
{
    ValueTask<CachedToken> GetTokenAsync(
        long installationId,
        string owner,
        string repo,
        CancellationToken cancellationToken);

    /// <summary>
    /// Invalidates any cached IAT for the supplied (installation, repo). Used
    /// by <see cref="IInstallationResolver"/> when it observes that a stale
    /// installation ID has been replaced.
    /// </summary>
    void Invalidate(long installationId, string repo);
}
