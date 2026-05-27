using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using MultiRepoMcp.IntegrationTests.Support;

namespace MultiRepoMcp.IntegrationTests;

/// <summary>
/// Verifies the bearer + allowlist + host filtering pipeline rejects bad
/// requests with the right status codes, all the way through to /mcp.
/// </summary>
public class AuthPipelineTests : IClassFixture<MultiRepoMcpFactory>
{
    private const string ToolsListJson = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";

    private readonly MultiRepoMcpFactory _factory;

    public AuthPipelineTests(MultiRepoMcpFactory factory) => _factory = factory;

    [Fact]
    public async Task Missing_bearer_returns_401()
    {
        var client = _factory.CreateClient();
        using var resp = await PostMcpAsync(client, ToolsListJson);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Wrong_bearer_returns_401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "not-the-real-token-but-long-enough");

        using var resp = await PostMcpAsync(client, ToolsListJson);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Allowlist_deny_returns_403()
    {
        using var factory = new MultiRepoMcpFactory
        {
            CallerAllowlist = new[] { "approved/repo" },
        };

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MultiRepoMcpFactory.BearerToken);
        client.DefaultRequestHeaders.Add("X-Caller-Repository", "rando/intruder");

        using var resp = await PostMcpAsync(client, ToolsListJson);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Allowlist_missing_header_returns_403_when_enabled()
    {
        using var factory = new MultiRepoMcpFactory
        {
            CallerAllowlist = new[] { "approved/repo" },
        };

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MultiRepoMcpFactory.BearerToken);

        using var resp = await PostMcpAsync(client, ToolsListJson);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Bearer_plus_allowlisted_caller_passes_auth_pipeline()
    {
        using var factory = new MultiRepoMcpFactory
        {
            CallerAllowlist = new[] { "approved/repo" },
        };

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MultiRepoMcpFactory.BearerToken);
        client.DefaultRequestHeaders.Add("X-Caller-Repository", "approved/repo");

        // We don't assert a specific success code (the MCP handler may want a
        // proper initialize flow first); the key invariant is "auth pipeline
        // didn't bounce us with 401/403".
        using var resp = await PostMcpAsync(client, ToolsListJson);
        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        resp.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    private static async Task<HttpResponseMessage> PostMcpAsync(HttpClient client, string jsonBody)
    {
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return await client.PostAsync(new Uri("/mcp", UriKind.Relative), content);
    }
}

