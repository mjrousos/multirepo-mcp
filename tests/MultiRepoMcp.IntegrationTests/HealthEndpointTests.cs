using System.Net;
using FluentAssertions;
using MultiRepoMcp.IntegrationTests.Support;

namespace MultiRepoMcp.IntegrationTests;

public class HealthEndpointTests : IClassFixture<MultiRepoMcpFactory>
{
    private readonly MultiRepoMcpFactory _factory;

    public HealthEndpointTests(MultiRepoMcpFactory factory) => _factory = factory;

    [Fact]
    public async Task Liveness_returns_200_with_no_dependency_calls()
    {
        // Note: prior tests in the same class fixture may have hit /app already.
        // Capture the count before, then assert no additional GitHub call occurs.
        _factory.WireMock.StubAppMetadata();
        var beforeCalls = _factory.WireMock.LogEntries.Count;

        var client = _factory.CreateClient();
        using var resp = await client.GetAsync(new Uri("/health/live", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        _factory.WireMock.LogEntries.Count.Should().Be(beforeCalls,
            "liveness must not touch GitHub or Key Vault");
    }
}
