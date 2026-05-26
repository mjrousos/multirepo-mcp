using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MultiRepoMcp.IntegrationTests.Support;
using WireMock.RequestBuilders;

namespace MultiRepoMcp.IntegrationTests;

/// <summary>
/// End-to-end check that <c>search_code</c> mints a repo-scoped IAT and
/// scopes the search query to the target repo. The repo-scoping assertion
/// is the security-critical one: even a malicious query string cannot reach
/// other repos because both (a) the IAT is repo-scoped and (b) the query
/// carries a <c>repo:owner/name</c> qualifier.
/// </summary>
public class SearchCodeFlowTests
{
    [Fact]
    public async Task search_code_scopes_iat_and_query_to_target_repo()
    {
        using var factory = new MultiRepoMcpFactory();
        factory.WireMock.StubInstallationDiscovery("octo", "hello", installationId: 77);
        factory.WireMock.StubTokenMint(
            installationId: 77,
            token: "ghs_search-token",
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(60));
        factory.WireMock.StubSearchCode("octo", "hello");

        using var client = factory.CreateAuthenticatedClient();
        var rpc = """
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{
              "name":"search_code",
              "arguments":{"owner":"octo","repo":"hello","query":"Console.WriteLine"}}}
            """;
        var resp = await PostMcpAsync(client, rpc);
        var body = await ReadMcpBodyAsync(resp);

        // Sanity: the response is a non-error MCP result envelope.
        body.Should().NotContain("\"error\":\"InternalError\"");
        body.Should().Contain("total_count");

        // 1. IAT mint was scoped to the target repo only.
        var tokenCalls = factory.WireMock
            .FindLogEntries(Request.Create()
                .WithPath("/app/installations/77/access_tokens")
                .UsingPost())
            .ToArray();
        tokenCalls.Should().HaveCount(1);
        var mintBody = tokenCalls[0].RequestMessage.Body ?? string.Empty;
        using var mintDoc = JsonDocument.Parse(mintBody);
        var repos = mintDoc.RootElement.GetProperty("repositories")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        repos.Should().BeEquivalentTo(new[] { "hello" });

        // 2. The outbound /search/code query carried a repo:owner/name qualifier.
        var searchCalls = factory.WireMock
            .FindLogEntries(Request.Create()
                .WithPath("/search/code")
                .UsingGet())
            .ToArray();
        searchCalls.Should().HaveCount(1);
        var queryValues = searchCalls[0].RequestMessage.Query!["q"];
        queryValues.Should().HaveCount(1);
        // WireMock decodes the URL form value. The Octokit SearchCodeRequest serializes
        // Repos as a "repo:owner/name" qualifier appended to the user-provided query.
        var q = queryValues[0];
        q.Should().Contain("Console.WriteLine");
        q.Should().MatchRegex(@"repo:octo/hello",
            "the query MUST scope results to the target repo via a repo:owner/name qualifier");
    }

    private static async Task<HttpResponseMessage> PostMcpAsync(HttpClient client, string jsonRpc)
    {
        using var content = new StringContent(jsonRpc, Encoding.UTF8, "application/json");
        var req = new HttpRequestMessage(HttpMethod.Post, new Uri("/mcp", UriKind.Relative))
        {
            Content = content,
        };
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return await client.SendAsync(req);
    }

    private static async Task<string> ReadMcpBodyAsync(HttpResponseMessage resp)
    {
        var raw = await resp.Content.ReadAsStringAsync();
        var mediaType = resp.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var line in raw.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                if (trimmed.StartsWith("data:", StringComparison.Ordinal))
                {
                    return trimmed["data:".Length..].TrimStart();
                }
            }
        }
        return raw;
    }
}
