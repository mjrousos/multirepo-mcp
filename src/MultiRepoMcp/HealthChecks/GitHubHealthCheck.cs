using System.Net;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MultiRepoMcp.Configuration;
using MultiRepoMcp.GitHub;
using Octokit;

namespace MultiRepoMcp.HealthChecks;

/// <summary>
/// Readiness check: ensures the GitHub App can mint a JWT and reach
/// <c>GET /app</c>. Result is cached for <see cref="CacheOptions.HealthCheckResultTtl"/>
/// to keep the rate-limit cost bounded regardless of probe frequency, with a
/// per-check <see cref="CacheOptions.HealthCheckDependencyTimeout"/>.
/// </summary>
internal sealed class GitHubHealthCheck : IHealthCheck, IDisposable
{
    private readonly IGitHubClientFactory _clientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly CacheOptions _cacheOptions;
    private readonly ILogger<GitHubHealthCheck> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private HealthCheckResult _cachedResult = HealthCheckResult.Unhealthy("Not yet checked.");

    public GitHubHealthCheck(
        IGitHubClientFactory clientFactory,
        TimeProvider timeProvider,
        IOptions<CacheOptions> cacheOptions,
        ILogger<GitHubHealthCheck> logger)
    {
        _clientFactory = clientFactory;
        _timeProvider = timeProvider;
        _cacheOptions = cacheOptions.Value;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        if (now - _cachedAt < _cacheOptions.HealthCheckResultTtl)
        {
            return _cachedResult;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (now - _cachedAt < _cacheOptions.HealthCheckResultTtl)
            {
                return _cachedResult;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_cacheOptions.HealthCheckDependencyTimeout);

            try
            {
                var client = await _clientFactory.CreateAppJwtClientAsync(cts.Token).ConfigureAwait(false);
                // Octokit's GitHubApps.GetCurrent() doesn't accept a
                // CancellationToken — its underlying HttpClient.Timeout is
                // 100s. Bound the wait ourselves with WaitAsync so a slow
                // GitHub doesn't pin the semaphore here for ~100s and force
                // orchestrator probes into queueing. The orphan HTTP request
                // continues in the background but is harmless.
                var probe = client.GitHubApps.GetCurrent();
                _ = await probe.WaitAsync(cts.Token).ConfigureAwait(false);
                _cachedResult = HealthCheckResult.Healthy();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("GitHub health check timed out.");
                _cachedResult = HealthCheckResult.Unhealthy("GitHub health check timed out.");
            }
            catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger.LogError(ex, "GitHub health check failed: App JWT rejected.");
                _cachedResult = HealthCheckResult.Unhealthy("GitHub App JWT rejected.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GitHub health check failed.");
                _cachedResult = HealthCheckResult.Unhealthy("GitHub API call failed.");
            }

            _cachedAt = _timeProvider.GetUtcNow();
            return _cachedResult;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
