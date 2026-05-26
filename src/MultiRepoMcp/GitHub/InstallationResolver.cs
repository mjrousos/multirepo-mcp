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

        long previousId = 0;
        if (_cache.TryGetValue(key, out var existingRaw) && existingRaw is CachedEntry existing && !existing.NotFound)
        {
            previousId = existing.InstallationId;
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

        if (previousId != 0 && previousId != installationId)
        {
            _logger.LogInformation(
                "Installation ID for {Owner}/{Repo} changed from {OldId} to {NewId}; invalidating IAT cache.",
                owner, repo, previousId, installationId);
            _tokenCache.Invalidate(previousId, repo);
        }

        return installationId;
    }

    private static string MakeKey(string owner, string repo) =>
        $"installation:{owner.ToLowerInvariant()}/{repo.ToLowerInvariant()}";

    private sealed record CachedEntry(long InstallationId, bool NotFound);
}
