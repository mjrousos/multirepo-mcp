using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using MultiRepoMcp.Configuration;
using MultiRepoMcp.GitHub;
using MultiRepoMcp.GitHub.Exceptions;
using Octokit;

namespace MultiRepoMcp.UnitTests.GitHub;

/// <summary>
/// Concurrency tests for the IAT cache — the highest-risk component in the
/// system. Covers single-flight, refresh failure cleanup, caller cancellation
/// isolation, proactive refresh, and the 422-on-mint → AppNotInstalled mapping.
/// </summary>
public class InstallationTokenCacheTests
{
    private static GitHubAppOptions DefaultOptions => new()
    {
        AppId = 12345,
        KeyVaultUri = new Uri("https://example.vault.azure.net/"),
        PrivateKeyName = "key",
        InstallationTokenRefreshThreshold = TimeSpan.FromMinutes(5),
        InstallationTokenRefreshJitter = TimeSpan.FromSeconds(30),
    };

    [Fact]
    public async Task Single_flight_collapses_N_concurrent_gets_into_one_refresh()
    {
        var factory = new RecordingClientFactory(
            (_, _, now) => new AccessToken("token-1", now.AddHours(1)),
            blockUntilReleased: true);

        var cache = NewCache(factory, DefaultOptions);

        var inflight = Enumerable.Range(0, 10)
            .Select(_ => cache.GetTokenAsync(42, "octo", "hello", CancellationToken.None).AsTask())
            .ToArray();

        await Task.Delay(50);
        factory.Release();

        var tokens = await Task.WhenAll(inflight);
        tokens.Should().AllSatisfy(t => t.Token.Should().Be("token-1"));
        factory.MintCount.Should().Be(1);
    }

    [Fact]
    public async Task Different_keys_refresh_independently()
    {
        var factory = new RecordingClientFactory(
            (installationId, repo, now) => new AccessToken($"t-{installationId}-{repo}", now.AddHours(1)),
            blockUntilReleased: false);

        var cache = NewCache(factory, DefaultOptions);

        var a = await cache.GetTokenAsync(1, "octo", "alpha", CancellationToken.None);
        var b = await cache.GetTokenAsync(1, "octo", "beta", CancellationToken.None);
        var c = await cache.GetTokenAsync(2, "octo", "alpha", CancellationToken.None);

        a.Token.Should().Be("t-1-alpha");
        b.Token.Should().Be("t-1-beta");
        c.Token.Should().Be("t-2-alpha");
        factory.MintCount.Should().Be(3);
    }

    [Fact]
    public async Task Refresh_failure_evicts_entry_so_next_call_retries()
    {
        var attempt = 0;
        var factory = new RecordingClientFactory(
            (_, _, now) =>
            {
                attempt++;
                if (attempt == 1)
                {
                    throw new InvalidOperationException("simulated transient failure");
                }
                return new AccessToken("token-after-recovery", now.AddHours(1));
            },
            blockUntilReleased: false);

        var cache = NewCache(factory, DefaultOptions);

        await FluentActions
            .Invoking(() => cache.GetTokenAsync(7, "octo", "repo", CancellationToken.None).AsTask())
            .Should().ThrowAsync<InvalidOperationException>();

        var second = await cache.GetTokenAsync(7, "octo", "repo", CancellationToken.None);
        second.Token.Should().Be("token-after-recovery");
    }

    [Fact]
    public async Task Caller_cancellation_does_not_abort_shared_refresh_for_peers()
    {
        var factory = new RecordingClientFactory(
            (_, _, now) => new AccessToken("shared", now.AddHours(1)),
            blockUntilReleased: true);

        var cache = NewCache(factory, DefaultOptions);

        using var cancellingCts = new CancellationTokenSource();
        var cancelling = cache.GetTokenAsync(11, "octo", "repo", cancellingCts.Token).AsTask();
        var patient = cache.GetTokenAsync(11, "octo", "repo", CancellationToken.None).AsTask();

        await Task.Delay(50);
        cancellingCts.Cancel();
        factory.Release();

        await FluentActions.Invoking(() => cancelling).Should().ThrowAsync<OperationCanceledException>();
        var peerToken = await patient;
        peerToken.Token.Should().Be("shared");
        factory.MintCount.Should().Be(1);
    }

    [Fact]
    public async Task IAT_mint_422_is_translated_to_AppNotInstalled()
    {
        var ex = new ApiException("simulated", System.Net.HttpStatusCode.UnprocessableEntity);
        var factory = new RecordingClientFactory(
            (_, _, _) => throw ex,
            blockUntilReleased: false);

        var cache = NewCache(factory, DefaultOptions);

        await FluentActions
            .Invoking(() => cache.GetTokenAsync(9, "octo", "repo", CancellationToken.None).AsTask())
            .Should().ThrowAsync<AppNotInstalledException>();
    }

    [Fact]
    public async Task Proactive_refresh_runs_when_cached_token_is_within_threshold()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        var seq = 0;
        var factory = new RecordingClientFactory(
            (_, _, now) =>
            {
                seq++;
                return new AccessToken($"t{seq}", now.AddHours(1));
            },
            blockUntilReleased: false,
            timeProvider: time);

        var cache = NewCacheWithTime(factory, DefaultOptions, time);

        var first = await cache.GetTokenAsync(1, "octo", "repo", CancellationToken.None);
        first.Token.Should().Be("t1");

        // Advance time so the cached token has < (threshold + jitter) remaining.
        time.Advance(TimeSpan.FromMinutes(56));

        var second = await cache.GetTokenAsync(1, "octo", "repo", CancellationToken.None);
        second.Token.Should().Be("t2");
        factory.MintCount.Should().Be(2);
    }

    [Fact]
    public async Task Cached_token_within_lifetime_is_returned_without_refresh()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2030-01-01T00:00:00Z"));
        var factory = new RecordingClientFactory(
            (_, _, now) => new AccessToken("only-token", now.AddHours(1)),
            blockUntilReleased: false,
            timeProvider: time);

        var cache = NewCacheWithTime(factory, DefaultOptions, time);

        await cache.GetTokenAsync(1, "octo", "repo", CancellationToken.None);
        // Stay well inside the cached token's lifetime; no refresh expected.
        time.Advance(TimeSpan.FromMinutes(10));

        for (var i = 0; i < 5; i++)
        {
            var t = await cache.GetTokenAsync(1, "octo", "repo", CancellationToken.None);
            t.Token.Should().Be("only-token");
        }

        factory.MintCount.Should().Be(1);
    }

    [Fact]
    public void Invalidate_removes_cached_entry()
    {
        var factory = new RecordingClientFactory(
            (_, _, now) => new AccessToken("t", now.AddHours(1)),
            blockUntilReleased: false);

        var cache = NewCache(factory, DefaultOptions);

        cache.GetTokenAsync(1, "octo", "repo", CancellationToken.None).AsTask().Wait();
        cache.Keys.Should().HaveCount(1);

        cache.Invalidate(1, "repo");
        cache.Keys.Should().BeEmpty();
    }

    private static InstallationTokenCache NewCache(IGitHubClientFactory factory, GitHubAppOptions options)
        => new(
            factory,
            Options.Create(options),
            TimeProvider.System,
            NullLogger<InstallationTokenCache>.Instance);

    private static InstallationTokenCache NewCacheWithTime(IGitHubClientFactory factory, GitHubAppOptions options, TimeProvider time)
        => new(
            factory,
            Options.Create(options),
            time,
            NullLogger<InstallationTokenCache>.Instance);

    /// <summary>
    /// Stand-in for <see cref="IGitHubClientFactory"/> that intercepts the
    /// access-token mint POST. The factory returns an Octokit client whose
    /// <c>Connection.Post&lt;AccessToken&gt;</c> calls the supplied delegate
    /// and (optionally) blocks until <see cref="Release"/> is invoked so the
    /// test can deliberately overlap concurrent callers.
    /// </summary>
    private sealed class RecordingClientFactory : IGitHubClientFactory
    {
        private readonly Func<long, string, DateTimeOffset, AccessToken> _mint;
        private readonly bool _blockUntilReleased;
        private readonly TimeProvider _timeProvider;
        private readonly TaskCompletionSource _release = new();
        private int _mintCount;

        public RecordingClientFactory(
            Func<long, string, DateTimeOffset, AccessToken> mint,
            bool blockUntilReleased,
            TimeProvider? timeProvider = null)
        {
            _mint = mint;
            _blockUntilReleased = blockUntilReleased;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        public int MintCount => _mintCount;

        public void Release() => _release.TrySetResult();

        public ValueTask<IGitHubClient> CreateAppJwtClientAsync(CancellationToken cancellationToken)
        {
            var connection = new Mock<IConnection>();
            connection
                .Setup(c => c.Post<AccessToken>(
                    It.IsAny<Uri>(),
                    It.IsAny<object>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<TimeSpan>(),
                    It.IsAny<CancellationToken>()))
                .Returns<Uri, object, string, string, TimeSpan, CancellationToken>(async (uri, body, _, _, _, _) =>
                {
                    if (_blockUntilReleased)
                    {
                        await _release.Task.ConfigureAwait(false);
                    }

                    Interlocked.Increment(ref _mintCount);

                    var segments = uri.OriginalString.Split('/');
                    var installationId = long.Parse(segments[2]);

                    var repos = (IEnumerable<string>)body.GetType().GetProperty("Repositories")!.GetValue(body)!;
                    var repo = repos.First();

                    var token = _mint(installationId, repo, _timeProvider.GetUtcNow());
                    var response = new Mock<IApiResponse<AccessToken>>();
                    response.SetupGet(r => r.Body).Returns(token);
                    return response.Object;
                });

            var clientMock = new Mock<IGitHubClient>();
            clientMock.SetupGet(c => c.Connection).Returns(connection.Object);
            return ValueTask.FromResult(clientMock.Object);
        }

        public IGitHubClient CreateInstallationClient(string installationToken) =>
            throw new NotImplementedException("Not exercised in IAT cache tests.");
    }
}
