using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MultiRepoMcp.Configuration;
using MultiRepoMcp.GitHub.Exceptions;
using Octokit;

namespace MultiRepoMcp.GitHub;

internal sealed class InstallationResolver : IInstallationResolver
{
    private readonly IGitHubClientFactory _clientFactory;
    private readonly IInstallationTokenCache _tokenCache;
    private readonly IMemoryCache _cache;
    private readonly CacheOptions _cacheOptions;
    private readonly ILogger<InstallationResolver> _logger;

    // Permanent side table (no TTL) that records the last installation ID we
    // observed for each owner/repo. Required for the plan's stale-installation
    // invalidation: IMemoryCache evicts on TTL, so by the time a refresh
    // discovers a new ID, the old ID is no longer in _cache. Keeping a
    // separate persistent record lets us still detect "the installation ID
    // for octo/cat just changed from 10 to 20" after the App was uninstalled
    // and reinstalled.
    private readonly ConcurrentDictionary<string, long> _lastSeenInstallationIds = new(StringComparer.Ordinal);

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
        // here uses a SEPARATE persistent table because IMemoryCache has
        // already evicted the old entry by the time we get here on TTL refresh.
        if (_lastSeenInstallationIds.TryGetValue(key, out var previousId)
            && previousId != installationId)
        {
            _logger.LogInformation(
                "Installation ID for {Owner}/{Repo} changed from {OldId} to {NewId}; invalidating IAT cache.",
                owner, repo, previousId, installationId);
            _tokenCache.Invalidate(previousId, repo);
        }
        _lastSeenInstallationIds[key] = installationId;

        return installationId;
    }

    private static string MakeKey(string owner, string repo) =>
        $"installation:{owner.ToLowerInvariant()}/{repo.ToLowerInvariant()}";

    private sealed record CachedEntry(long InstallationId, bool NotFound);
}
