using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MultiRepoMcp.HealthChecks;

internal static class HealthCheckEndpointExtensions
{
    private const string LivenessTag = "live";
    private const string ReadinessTag = "ready";
    private const string StartupTag = "startup";

    public static IServiceCollection AddMultiRepoMcpHealthChecks(this IServiceCollection services)
    {
        // Registering the checks as singletons so the per-check result cache
        // (DateTimeOffset _cachedAt + HealthCheckResult _cachedResult) is
        // shared across probes. AddCheck<T> would otherwise activate a fresh
        // instance per call via ActivatorUtilities, defeating the cache.
        services.AddSingleton<GitHubHealthCheck>();
        services.AddSingleton<KeyVaultHealthCheck>();

        services.AddHealthChecks()
            // Dependency-free liveness probe. Orchestrators MUST NOT restart the
            // container during transient GitHub or Key Vault blips.
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { LivenessTag })
            .AddCheck<GitHubHealthCheck>("github", tags: new[] { ReadinessTag, StartupTag })
            .AddCheck<KeyVaultHealthCheck>("keyvault", tags: new[] { ReadinessTag, StartupTag });

        return services;
    }

    public static IEndpointRouteBuilder MapMultiRepoMcpHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", BuildOptions(LivenessTag)).AllowAnonymous();
        endpoints.MapHealthChecks("/health/ready", BuildOptions(ReadinessTag)).AllowAnonymous();
        endpoints.MapHealthChecks("/health/startup", BuildOptions(StartupTag)).AllowAnonymous();

        return endpoints;

        static HealthCheckOptions BuildOptions(string tag) => new()
        {
            Predicate = registration => registration.Tags.Contains(tag),
            ResponseWriter = WriteSanitizedResponse,
        };
    }

    /// <summary>
    /// Emits only <c>{"status":"Healthy|Degraded|Unhealthy"}</c>. Detailed
    /// per-check diagnostics (which dependency failed and why) go to the
    /// server log, never to the response body — to keep operational signal
    /// off the public surface.
    /// </summary>
    private static Task WriteSanitizedResponse(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var payload = JsonSerializer.Serialize(new { status = report.Status.ToString() });
        return context.Response.WriteAsync(payload);
    }
}
