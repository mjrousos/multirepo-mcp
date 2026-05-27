using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MultiRepoMcp.Configuration;
using MultiRepoMcp.GitHub;
using MultiRepoMcp.GitHub.Exceptions;
using Octokit;

namespace MultiRepoMcp.UnitTests.GitHub;

/// <summary>
/// Tests for <see cref="InstallationResolver"/>. The most security-relevant
/// scenario is stale-installation-ID detection (plan §2.5): if a user
/// uninstalls then reinstalls the App so the installation ID changes for
/// the same owner/repo, the resolver must invalidate any IAT we still hold
/// under the old installation ID, otherwise we'll keep handing out tokens
/// that GitHub rejects.
/// </summary>
public sealed class InstallationResolverTests : IDisposable
{
    private IMemoryCache? _memoryCache;

    public void Dispose()
    {
        _memoryCache?.Dispose();
        GC.SuppressFinalize(this);
    }

    private static CacheOptions DefaultCache => new()
    {
        InstallationDiscoveryTtl = TimeSpan.FromHours(1),
        InstallationNotFoundTtl = TimeSpan.FromSeconds(60),
    };

    [Fact]
    public async Task Cache_hit_returns_without_calling_github()
    {
        var clientFactory = new FakeClientFactory();
        clientFactory.OnLookup("octo", "hello", id: 42);

        var tokenCache = new Mock<IInstallationTokenCache>();
        var resolver = NewResolver(clientFactory, tokenCache.Object);

        var first = await resolver.ResolveAsync("octo", "hello", CancellationToken.None);
        var second = await resolver.ResolveAsync("octo", "hello", CancellationToken.None);

        first.Should().Be(42);
        second.Should().Be(42);
        clientFactory.LookupCount.Should().Be(1, "the second call should hit the cache");
    }

    [Fact]
    public async Task Cache_miss_then_hit_after_TTL_does_not_change_installation_does_not_invalidate()
    {
        var clientFactory = new FakeClientFactory();
        clientFactory.OnLookup("octo", "hello", id: 42);

        var tokenCache = new Mock<IInstallationTokenCache>();
        var resolver = NewResolver(clientFactory, tokenCache.Object);

        // Two fresh lookups (we don't actually expire the cache; we just drive
        // ResolveAsync twice through the live-path by clearing the memory
        // cache). Same installation ID both times → no invalidation.
        await resolver.ResolveAsync("octo", "hello", CancellationToken.None);
        // Clear IMemoryCache to force a refresh on the second call.
        ((MemoryCache)_memoryCache!).Clear();
        await resolver.ResolveAsync("octo", "hello", CancellationToken.None);

        tokenCache.Verify(
            t => t.Invalidate(It.IsAny<long>(), It.IsAny<string>()),
            Times.Never,
            "no invalidation when the installation ID does not change");
    }

    [Fact]
    public async Task Stale_installation_id_invalidates_old_iat_cache_entry()
    {
        // Reinstall scenario: octo/hello was installation 10, then admin
        // uninstalls and re-installs, producing installation 20. The
        // resolver must call Invalidate(10, "hello") on the IAT cache so
        // we don't keep handing out tokens minted under the old installation.
        var clientFactory = new FakeClientFactory();
        clientFactory.OnLookup("octo", "hello", id: 10);

        var tokenCache = new Mock<IInstallationTokenCache>();
        var resolver = NewResolver(clientFactory, tokenCache.Object);

        var first = await resolver.ResolveAsync("octo", "hello", CancellationToken.None);
        first.Should().Be(10);

        // Expire the discovery cache (simulates TTL elapsing) and switch
        // the GitHub side to return the new installation ID.
        ((MemoryCache)_memoryCache!).Clear();
        clientFactory.OnLookup("octo", "hello", id: 20);

        var second = await resolver.ResolveAsync("octo", "hello", CancellationToken.None);
        second.Should().Be(20);

        tokenCache.Verify(
            t => t.Invalidate(10, "hello"),
            Times.Once,
            "old installation IAT must be invalidated when the installation ID changes");
    }

    [Fact]
    public async Task Not_found_throws_AppNotInstalled_and_is_negatively_cached()
    {
        var clientFactory = new FakeClientFactory();
        clientFactory.OnNotFound("octo", "hello");

        var tokenCache = new Mock<IInstallationTokenCache>();
        var resolver = NewResolver(clientFactory, tokenCache.Object);

        Func<Task> firstCall = () => resolver.ResolveAsync("octo", "hello", CancellationToken.None).AsTask();
        await firstCall.Should().ThrowAsync<AppNotInstalledException>();

        // Second call should NOT call GitHub again — the negative result is cached.
        Func<Task> secondCall = () => resolver.ResolveAsync("octo", "hello", CancellationToken.None).AsTask();
        await secondCall.Should().ThrowAsync<AppNotInstalledException>();

        clientFactory.LookupCount.Should().Be(1, "the negative result must be cached");
    }

    private InstallationResolver NewResolver(IGitHubClientFactory factory, IInstallationTokenCache tokenCache)
    {
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
        return new InstallationResolver(
            factory,
            tokenCache,
            _memoryCache,
            Options.Create(DefaultCache),
            NullLogger<InstallationResolver>.Instance);
    }

    /// <summary>
    /// Mocks <see cref="IGitHubClient.GitHubApps"/> to return a fixed
    /// <see cref="Installation"/> for the configured owner/repo, or throw
    /// <see cref="NotFoundException"/> when no result has been configured.
    /// </summary>
    private sealed class FakeClientFactory : IGitHubClientFactory
    {
        private readonly Dictionary<string, long> _ids = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _notFound = new(StringComparer.OrdinalIgnoreCase);
        private int _lookupCount;

        public int LookupCount => _lookupCount;

        public void OnLookup(string owner, string repo, long id)
        {
            _ids[$"{owner}/{repo}"] = id;
            _notFound.Remove($"{owner}/{repo}");
        }

        public void OnNotFound(string owner, string repo)
        {
            _notFound.Add($"{owner}/{repo}");
            _ids.Remove($"{owner}/{repo}");
        }

        public ValueTask<IGitHubClient> CreateAppJwtClientAsync(CancellationToken cancellationToken)
        {
            var appsClient = new Mock<IGitHubAppsClient>();
            appsClient
                .Setup(a => a.GetRepositoryInstallationForCurrent(It.IsAny<string>(), It.IsAny<string>()))
                .Returns<string, string>((owner, repo) =>
                {
                    Interlocked.Increment(ref _lookupCount);
                    var key = $"{owner}/{repo}";
                    if (_notFound.Contains(key))
                    {
                        throw new NotFoundException("Not found", System.Net.HttpStatusCode.NotFound);
                    }
                    if (_ids.TryGetValue(key, out var id))
                    {
                        // Octokit's Installation only exposes a long-args
                        // constructor and several string params are wrapped
                        // in StringEnum (which rejects null/empty). The
                        // resolver only reads Id, so pass safe placeholders.
                        return Task.FromResult(new Installation(
                            id,
                            account: null!,
                            accessTokensUrl: string.Empty,
                            repositoriesUrl: string.Empty,
                            htmlUrl: string.Empty,
                            appId: 0,
                            targetId: 0,
                            targetType: AccountType.Organization,
                            permissions: new InstallationPermissions(),
                            events: new List<string>(),
                            singleFileName: string.Empty,
                            repositorySelection: "all",
                            suspendedBy: null!,
                            suspendedAt: null));
                    }
                    throw new NotFoundException("Not configured", System.Net.HttpStatusCode.NotFound);
                });

            var client = new Mock<IGitHubClient>();
            client.SetupGet(c => c.GitHubApps).Returns(appsClient.Object);
            return ValueTask.FromResult(client.Object);
        }

        public IGitHubClient CreateInstallationClient(string installationToken)
            => throw new NotImplementedException("Not used by InstallationResolver.");
    }
}
