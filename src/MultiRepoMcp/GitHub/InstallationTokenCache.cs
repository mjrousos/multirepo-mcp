using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MultiRepoMcp.Configuration;
using MultiRepoMcp.GitHub.Exceptions;
using Octokit;

namespace MultiRepoMcp.GitHub;

/// <summary>
/// Per-(installationId, repoNameLower) IAT cache with single-flight refresh.
/// Tokens are minted with <c>repositories: [repo]</c> so each token is scoped
/// to a single repository at GitHub's authoritative boundary.
/// </summary>
internal sealed class InstallationTokenCache : IInstallationTokenCache
{
    private readonly ConcurrentDictionary<(long InstallationId, string Repo), Lazy<Task<CachedToken>>> _cache = new();
    private readonly IGitHubClientFactory _clientFactory;
    private readonly GitHubAppOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InstallationTokenCache> _logger;

    public InstallationTokenCache(
        IGitHubClientFactory clientFactory,
        IOptions<GitHubAppOptions> options,
        TimeProvider timeProvider,
        ILogger<InstallationTokenCache> logger)
    {
        _clientFactory = clientFactory;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask<CachedToken> GetTokenAsync(
        long installationId,
        string owner,
        string repo,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        if (installationId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(installationId), installationId, "Installation id must be > 0.");
        }

        var key = MakeKey(installationId, repo);

        // Read/refresh loop: tolerates the TOCTOU between observing a near-expiry
        // entry and replacing it (concurrent callers may both reach the replace
        // step; TryUpdate compares against the observed Lazy so only the winner's
        // newLazy installs, and the losers re-read on the next loop iteration).
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var current = _cache.GetOrAdd(
                key,
                _ => CreateRefreshLazy(installationId, owner, repo));

            CachedToken token;
            try
            {
                // NB: we await the shared task with the caller's CT so that the
                // caller can abandon waiting; the underlying refresh runs under
                // CancellationToken.None and continues to completion for other
                // callers awaiting the same Lazy.
                token = await current.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Caller-driven cancellation; do NOT poison the cache entry —
                // other callers are still awaiting the shared refresh task.
                throw;
            }
            catch
            {
                // Remove only this specific Lazy (key+value comparand); leave
                // any peer's freshly-installed replacement untouched.
                _cache.TryRemove(new KeyValuePair<(long, string), Lazy<Task<CachedToken>>>(key, current));
                throw;
            }

            var now = _timeProvider.GetUtcNow();
            if (token.RemainingLifetime(now) > EffectiveRefreshThreshold(key))
            {
                return token;
            }

            // Try to swap in a fresh refresh; if TryUpdate fails, a peer already
            // installed a replacement — the next iteration picks it up.
            var replacement = CreateRefreshLazy(installationId, owner, repo);
            _cache.TryUpdate(key, replacement, current);
        }
    }

    public void Invalidate(long installationId, string repo)
    {
        if (installationId <= 0 || string.IsNullOrWhiteSpace(repo))
        {
            return;
        }

        _cache.TryRemove(MakeKey(installationId, repo), out _);
    }

    private Lazy<Task<CachedToken>> CreateRefreshLazy(long installationId, string owner, string repo) =>
        new(
            // CancellationToken.None: shared refresh work must NOT be tied to
            // any one caller's cancellation token.
            () => RefreshAsync(installationId, owner, repo, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication);

    private async Task<CachedToken> RefreshAsync(
        long installationId,
        string owner,
        string repo,
        CancellationToken cancellationToken)
    {
        var client = await _clientFactory.CreateAppJwtClientAsync(cancellationToken).ConfigureAwait(false);

        // Octokit 14.0's IGitHubAppsClient.CreateInstallationToken(long) does
        // not expose the body parameters we need to scope the token to a
        // single repo. Call the REST endpoint directly via IConnection.
        //
        // POST /app/installations/{installation_id}/access_tokens
        //   { "repositories": ["<repo>"] }
        //
        // Returns: { "token": "...", "expires_at": "..." }
        var uri = new Uri($"app/installations/{installationId}/access_tokens", UriKind.Relative);
        var body = new RepoScopedTokenRequest(new[] { repo });

        try
        {
            var response = await client.Connection
                .Post<AccessToken>(
                    uri,
                    body,
                    "application/vnd.github+json",
                    "application/json",
                    TimeSpan.FromSeconds(30),
                    cancellationToken)
                .ConfigureAwait(false);

            var accessToken = response.Body
                ?? throw new InvalidOperationException(
                    "GitHub returned an empty body from access-token mint.");

            return new CachedToken(accessToken.Token, accessToken.ExpiresAt);
        }
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            // 422 from the access-token mint indicates the named repo isn't
            // accessible to this installation — surface the same error as
            // installation-discovery 404.
            _logger.LogWarning(ex,
                "IAT mint returned 422 for installation {InstallationId}, repo {Repo}; treating as App not installed.",
                installationId, repo);
            throw new AppNotInstalledException(owner, repo, ex);
        }
    }

    private sealed record RepoScopedTokenRequest(IEnumerable<string> Repositories);

    /// <summary>
    /// Per-key deterministic jitter ∈ [0, <see cref="GitHubAppOptions.InstallationTokenRefreshJitter"/>]
    /// added to the configured refresh threshold so a large fleet of
    /// installations does not flip stale on the same clock tick.
    /// </summary>
    private TimeSpan EffectiveRefreshThreshold((long InstallationId, string Repo) key)
    {
        var jitterMax = _options.InstallationTokenRefreshJitter;
        if (jitterMax <= TimeSpan.Zero)
        {
            return _options.InstallationTokenRefreshThreshold;
        }

        // Deterministic per-key hash → milliseconds offset within [0, jitterMax).
        var seedBytes = Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{key.InstallationId}|{key.Repo}"));
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(seedBytes, hash);
        var seed = BitConverter.ToUInt32(hash[..4]);

        var jitterMs = (long)(seed % (ulong)jitterMax.TotalMilliseconds);
        return _options.InstallationTokenRefreshThreshold + TimeSpan.FromMilliseconds(jitterMs);
    }

    private static (long, string) MakeKey(long installationId, string repo) =>
        (installationId, repo.ToLowerInvariant());

    /// <summary>For tests only: snapshot of the current cache key set.</summary>
    internal IReadOnlyCollection<(long InstallationId, string Repo)> Keys =>
        _cache.Keys.ToArray();
}
