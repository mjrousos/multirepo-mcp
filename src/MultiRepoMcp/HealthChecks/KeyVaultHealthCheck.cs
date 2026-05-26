using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MultiRepoMcp.Configuration;

namespace MultiRepoMcp.HealthChecks;

/// <summary>
/// Readiness check that issues a single <c>GetSecretAsync</c> round-trip to
/// confirm the App's private-key secret is reachable. We use the get-secret
/// operation (which only needs the documented "Get" permission) and discard
/// the downloaded bytes; the parsed PEM is already in memory.
/// </summary>
internal sealed class KeyVaultHealthCheck : IHealthCheck, IDisposable
{
    private readonly GitHubAppOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly CacheOptions _cacheOptions;
    private readonly ILogger<KeyVaultHealthCheck> _logger;
    private readonly Lazy<SecretClient?> _secretClient;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private HealthCheckResult _cachedResult = HealthCheckResult.Unhealthy("Not yet checked.");

    public KeyVaultHealthCheck(
        IOptions<GitHubAppOptions> options,
        IOptions<CacheOptions> cacheOptions,
        TimeProvider timeProvider,
        ILogger<KeyVaultHealthCheck> logger,
        SecretClient? secretClientForTests = null)
    {
        _options = options.Value;
        _cacheOptions = cacheOptions.Value;
        _timeProvider = timeProvider;
        _logger = logger;

        _secretClient = new Lazy<SecretClient?>(() =>
            secretClientForTests
                ?? (_options.KeyVaultUri is null
                    ? null
                    : new SecretClient(_options.KeyVaultUri, new DefaultAzureCredential())));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // Dev override skips Key Vault entirely; nothing to check.
        if (!string.IsNullOrEmpty(_options.LocalPrivateKeyPath))
        {
            return HealthCheckResult.Healthy("Local private-key path configured.");
        }

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

            var client = _secretClient.Value;
            if (client is null)
            {
                _cachedResult = HealthCheckResult.Unhealthy("Key Vault URI is not configured.");
                _cachedAt = _timeProvider.GetUtcNow();
                return _cachedResult;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_cacheOptions.HealthCheckDependencyTimeout);

            try
            {
                // SecretClient does not expose a per-secret "get properties
                // without value" call — only GetSecretAsync (one secret,
                // requires Get permission) and GetPropertiesOfSecretVersionsAsync
                // (LIST operation, requires List permission). We choose Get
                // semantics so operators can grant the documented minimum
                // permission (Key Vault Secrets User / "Get") and this readiness
                // check still passes. The downloaded bytes are intentionally
                // discarded — the PEM is already loaded once at startup.
                _ = await client
                    .GetSecretAsync(_options.PrivateKeySecretName, cancellationToken: cts.Token)
                    .ConfigureAwait(false);
                _cachedResult = HealthCheckResult.Healthy();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Key Vault health check timed out.");
                _cachedResult = HealthCheckResult.Unhealthy("Key Vault health check timed out.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Key Vault health check failed.");
                _cachedResult = HealthCheckResult.Unhealthy("Key Vault metadata read failed.");
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
