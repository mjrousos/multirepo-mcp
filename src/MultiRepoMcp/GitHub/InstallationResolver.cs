using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MultiRepoMcp.Configuration;
using MultiRepoMcp.GitHub.Exceptions;
using Octokit;

namespace MultiRepoMcp.GitHub;

internal sealed class InstallationResolver : IInstallationResolver, IDisposable
{
    /// <summary>
    /// Hard cap on tracked owner/repo entries in the last-seen-installation
    /// side cache. Bounds memory growth for long-lived deployments serving
    /// many distinct repos. LRU-style eviction (least-recently-used by absolute
    /// expiration) kicks in past this many entries.
    /// </summary>
    private const long LastSeenSizeLimit = 10_000;

    private readonly IGitHubClientFactory _clientFactory;
    private readonly IInstallationTokenCache _tokenCache;
    private readonly IMemoryCache _cache;
    private readonly CacheOptions _cacheOptions;
    private readonly ILogger<InstallationResolver> _logger;

    // Dedicated side cache for last-seen installation IDs. Separate from
    // _cache so its entries survive callers clearing or compacting the
    // discovery cache, and because we set our own size/TTL bounds.
    // Required for the plan's stale-installation invalidation: the main
    // _cache evicts the prior installation ID once its discovery TTL
    // elapses, so a refresh that goes to GitHub would otherwise have no
    // basis for comparison. We store last-seen with a longer TTL
    // (2x InstallationDiscoveryTtl) so the value is still present when
    // the next refresh happens after the main entry's TTL.
    private readonly MemoryCache _lastSeenCache = new(new MemoryCacheOptions { SizeLimit = LastSeenSizeLimit });

    public InstallationResolver(
        IGitHubClientFactory clientFactory,
        IInstallationTokenCache tokenCache,
        IMemoryCache cache,
        IOptions<CacheOptions> cacheOptions,
        ILogger<InstallationResolver> logger)
    {
        _clientFactory = clientFactory;
        _tokenCache = tokenCache;
        _cache = cache;
        _cacheOptions = cacheOptions.Value;
        _logger = logger;
    }

    public async ValueTask<long> ResolveAsync(string owner, string repo, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        var key = MakeKey(owner, repo);

        if (_cache.TryGetValue(key, out var raw) && raw is CachedEntry entry)
        {
            if (entry.NotFound)
            {
                throw new AppNotInstalledException(owner, repo);
            }

            return entry.InstallationId;
        }

        long installationId;
        try
        {
            var client = await _clientFactory.CreateAppJwtClientAsync(cancellationToken).ConfigureAwait(false);
            var installation = await client.GitHubApps
                .GetRepositoryInstallationForCurrent(owner, repo)
                .ConfigureAwait(false);
            installationId = installation.Id;
        }
        catch (NotFoundException nfe)
        {
            _logger.LogInformation(
                "Installation discovery returned 404 for {Owner}/{Repo}; negative-caching for {Ttl}.",
                owner, repo, _cacheOptions.InstallationNotFoundTtl);

            _cache.Set(
                key,
                new CachedEntry(0, NotFound: true),
                _cacheOptions.InstallationNotFoundTtl);

            throw new AppNotInstalledException(owner, repo, nfe);
        }

        _cache.Set(
            key,
            new CachedEntry(installationId, NotFound: false),
            _cacheOptions.InstallationDiscoveryTtl);

        // Detect a changed installation ID for the same owner/repo (App
        // uninstalled then reinstalled elsewhere). When detected, evict any
        // stale IAT we may still hold under the old installation ID so we
        // don't keep handing out tokens that GitHub will reject. The lookup
        // here uses a SEPARATE bounded cache (_lastSeenCache) because the
        // main discovery cache has already evicted the old entry by the
        // time we get here on TTL refresh.
        var lastSeenKey = MakeLastSeenKey(owner, repo);
        if (_lastSeenCache.TryGetValue(lastSeenKey, out var lastSeenRaw)
            && lastSeenRaw is long previousId
            && previousId != installationId)
        {
            _logger.LogInformation(
                "Installation ID for {Owner}/{Repo} changed from {OldId} to {NewId}; invalidating IAT cache.",
                owner, repo, previousId, installationId);
            _tokenCache.Invalidate(previousId, repo);
        }
        _lastSeenCache.Set(
            lastSeenKey,
            installationId,
            new MemoryCacheEntryOptions
            {
                Size = 1,
                AbsoluteExpirationRelativeToNow =
                    TimeSpan.FromTicks(_cacheOptions.InstallationDiscoveryTtl.Ticks * 2),
            });

        return installationId;
    }

    public void Dispose() => _lastSeenCache.Dispose();

    private static string MakeKey(string owner, string repo) =>
        $"installation:{owner.ToLowerInvariant()}/{repo.ToLowerInvariant()}";

    private static string MakeLastSeenKey(string owner, string repo) =>
        $"{owner.ToLowerInvariant()}/{repo.ToLowerInvariant()}";

    private sealed record CachedEntry(long InstallationId, bool NotFound);
}
