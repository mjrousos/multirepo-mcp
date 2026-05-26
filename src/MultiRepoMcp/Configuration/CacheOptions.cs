namespace MultiRepoMcp.Configuration;

public sealed class CacheOptions
{
    public const string SectionName = "Cache";

    /// <summary>
    /// Positive cache TTL for installation discovery results
    /// (<c>owner/repo</c> → installation ID).
    /// </summary>
    public TimeSpan InstallationDiscoveryTtl { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Negative cache TTL for "App not installed" results. Defends against
    /// loop-driven callers hammering GitHub when the App is uninstalled.
    /// </summary>
    public TimeSpan InstallationNotFoundTtl { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Cache TTL for cached health-check results.</summary>
    public TimeSpan HealthCheckResultTtl { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Timeout applied to each dependency check inside the readiness probe.</summary>
    public TimeSpan HealthCheckDependencyTimeout { get; init; } = TimeSpan.FromSeconds(3);
}
