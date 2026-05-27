using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using MultiRepoMcp.IntegrationTests.Support;

namespace MultiRepoMcp.IntegrationTests;

/// <summary>
/// Confirms ASP.NET Core's <c>HostFilteringMiddleware</c> rejects requests
/// with an unknown <c>Host</c> header BEFORE the auth pipeline runs — even a
/// valid bearer must not get past host filtering. Defends against DNS rebinding.
/// </summary>
public class HostFilteringTests
{
    [Fact]
    public async Task Unknown_host_header_is_rejected_with_bad_request()
    {
        using var factory = new MultiRepoMcpFactory
        {
            AllowedHosts = "multirepo-mcp.example.com",
        };

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MultiRepoMcpFactory.BearerToken);
        client.DefaultRequestHeaders.Host = "evil.com";
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            Encoding.UTF8,
            "application/json");
        using var resp = await client.PostAsync(new Uri("/mcp", UriKind.Relative), content);

        // HostFilteringMiddleware rejects with 400 Bad Request — NOT 401, which
        // would only be set after auth had a chance to run.
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Allowed_host_header_passes_to_auth_pipeline()
    {
        using var factory = new MultiRepoMcpFactory
        {
            AllowedHosts = "multirepo-mcp.example.com;localhost",
        };

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Host = "multirepo-mcp.example.com";
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            Encoding.UTF8,
            "application/json");
        using var resp = await client.PostAsync(new Uri("/mcp", UriKind.Relative), content);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the allowed host should pass HostFiltering and be rejected by auth (no bearer)");
    }
}
