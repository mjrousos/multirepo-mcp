using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MultiRepoMcp.Configuration;
using MultiRepoMcp.GitHub;

namespace MultiRepoMcp.HealthChecks;

/// <summary>
/// Readiness check that exercises the configured signing pathway by asking
/// the active <see cref="IJwtSigner"/> to sign a fixed 32-byte digest. For
/// the production <c>KeyVaultJwtSigner</c> this means one Key Vault
/// <c>POST /sign</c> round-trip — which validates connectivity, AAD auth,
/// and the <c>Sign</c> key permission all in one call. For the local-PEM
/// signer used in dev/tests it's a microsecond in-process op.
///
/// Results are cached for <see cref="CacheOptions.HealthCheckResultTtl"/>
/// to bound load on Key Vault under aggressive probe schedules.
/// </summary>
internal sealed class KeyVaultHealthCheck : IHealthCheck, IDisposable
{
    // A fixed all-zero digest is fine for a liveness probe — Key Vault's
    // /sign endpoint validates the algorithm + permission regardless of
    // the input bytes, and we never expose the resulting signature.
    private static readonly byte[] ProbeDigest = new byte[32];

    private readonly IJwtSigner _signer;
    private readonly TimeProvider _timeProvider;
    private readonly CacheOptions _cacheOptions;
    private readonly ILogger<KeyVaultHealthCheck> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private HealthCheckResult _cachedResult = HealthCheckResult.Unhealthy("Not yet checked.");

    public KeyVaultHealthCheck(
        IJwtSigner signer,
        IOptions<CacheOptions> cacheOptions,
        TimeProvider timeProvider,
        ILogger<KeyVaultHealthCheck> logger)
    {
        _signer = signer;
        _cacheOptions = cacheOptions.Value;
        _timeProvider = timeProvider;
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
                // Wrap the signer call in WaitAsync so a slow / hung Key Vault
                // doesn't pin our semaphore for longer than the configured
                // dependency timeout (the in-flight HTTP call is orphaned
                // but bounded by HttpClient.Timeout in the Azure SDK).
                var signTask = _signer.SignDigestAsync(ProbeDigest, cts.Token).AsTask();
                _ = await signTask.WaitAsync(cts.Token).ConfigureAwait(false);
                _cachedResult = HealthCheckResult.Healthy();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Signing-key health check timed out.");
                _cachedResult = HealthCheckResult.Unhealthy("Signing-key health check timed out.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Signing-key health check failed.");
                _cachedResult = HealthCheckResult.Unhealthy("Signing-key probe failed.");
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
