using System.Net;
using FluentAssertions;
using MultiRepoMcp.IntegrationTests.Support;
using WireMock.RequestBuilders;

namespace MultiRepoMcp.IntegrationTests;

/// <summary>
/// Verifies that <c>/health/ready</c> caches its GitHub <c>GET /app</c> probe
/// so a flood of orchestrator probes does NOT burn the App's rate-limit budget.
/// </summary>
public class HealthCheckCachingTests
{
    [Fact]
    public async Task Sixty_readiness_probes_only_hit_GitHub_once_within_TTL()
    {
        using var factory = new MultiRepoMcpFactory();
        factory.WireMock.StubAppMetadata();

        var beforeAppCalls = factory.WireMock
            .FindLogEntries(Request.Create().WithPath("/app").UsingGet()).Count;

        var client = factory.CreateClient();
        for (int i = 0; i < 60; i++)
        {
            using var resp = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var afterAppCalls = factory.WireMock
            .FindLogEntries(Request.Create().WithPath("/app").UsingGet()).Count;

        (afterAppCalls - beforeAppCalls).Should().Be(1,
            "the 30-second result cache should collapse 60 probes into a single live GitHub call");
    }
}
