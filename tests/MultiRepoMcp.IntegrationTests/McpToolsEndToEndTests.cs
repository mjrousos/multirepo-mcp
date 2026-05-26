using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using MultiRepoMcp.IntegrationTests.Support;
using WireMock.Matchers;
using WireMock.RequestBuilders;

namespace MultiRepoMcp.IntegrationTests;

/// <summary>
/// Drives the MCP server via raw JSON-RPC POSTs against /mcp. The server is
/// configured stateless, so individual <c>tools/list</c> and <c>tools/call</c>
/// requests can be sent without an initialize handshake.
/// </summary>
public class McpToolsEndToEndTests
{
    [Fact]
    public async Task tools_list_returns_get_file_contents_and_search_code()
    {
        using var factory = new MultiRepoMcpFactory();
        using var client = factory.CreateAuthenticatedClient();

        var resp = await PostMcpAsync(client, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        var body = await ReadMcpBodyAsync(resp);

        using var doc = JsonDocument.Parse(body);
        var toolNames = doc.RootElement
            .GetProperty("result")
            .GetProperty("tools")
            .EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToHashSet(StringComparer.Ordinal);

        toolNames.Should().Contain("get_file_contents");
        toolNames.Should().Contain("search_code");
    }

    [Fact]
    public async Task get_file_contents_returns_PathIsDirectory_for_single_child_directory()
    {
        // Regression test: GitHub returns a JSON array listing for ANY
        // directory — including one that contains exactly one child. A
        // naive "contents.Count > 1" check would mis-classify this as the
        // file itself and return the child's content as if the directory
        // path had been a file. The fix compares the returned entry's Path
        // against the requested path.
        using var factory = new MultiRepoMcpFactory();
        factory.WireMock.StubInstallationDiscovery("octo", "hello", installationId: 99);
        factory.WireMock.StubTokenMint(
            installationId: 99,
            token: "ghs_test-token",
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(60));
        factory.WireMock.StubDirectoryListing("octo", "hello", "lonely-dir", "only-child.txt");

        using var client = factory.CreateAuthenticatedClient();
        var resp = await PostMcpAsync(client, BuildCall("octo", "hello", "lonely-dir"));
        var body = await ReadMcpBodyAsync(resp);

        body.Should().Contain("PathIsDirectory",
            "a single-entry directory listing must still be reported as a directory, not as the child file");
    }

    [Fact]
    public async Task get_file_contents_mints_repo_scoped_iat_and_returns_text()
    {
        using var factory = new MultiRepoMcpFactory();
        factory.WireMock.StubInstallationDiscovery("octo", "hello", installationId: 99);
        factory.WireMock.StubTokenMint(
            installationId: 99,
            token: "ghs_test-token",
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(60));
        factory.WireMock.StubFileContents("octo", "hello", "README.md", "Hello, world!");

        using var client = factory.CreateAuthenticatedClient();
        var rpc = """
            {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{
              "name":"get_file_contents",
              "arguments":{"owner":"octo","repo":"hello","path":"README.md"}}}
            """;
        var resp = await PostMcpAsync(client, rpc);
        var body = await ReadMcpBodyAsync(resp);

        body.Should().Contain("Hello, world!");

        // Verify the token-mint POST body scoped the IAT to the target repo.
        var tokenCalls = factory.WireMock
            .FindLogEntries(Request.Create()
                .WithPath("/app/installations/99/access_tokens")
                .UsingPost())
            .ToArray();

        tokenCalls.Should().HaveCount(1);
        var mintBody = tokenCalls[0].RequestMessage.Body ?? string.Empty;
        using var mintDoc = JsonDocument.Parse(mintBody);
        var repos = mintDoc.RootElement.GetProperty("repositories")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        repos.Should().BeEquivalentTo(new[] { "hello" });
    }

    [Fact]
    public async Task Cross_repo_within_installation_mints_independent_iats()
    {
        using var factory = new MultiRepoMcpFactory();
        factory.WireMock.StubInstallationDiscovery("octo", "alpha", installationId: 42);
        factory.WireMock.StubInstallationDiscovery("octo", "beta", installationId: 42);
        factory.WireMock.StubTokenMint(42, "ghs_alpha", DateTimeOffset.UtcNow.AddMinutes(60));
        factory.WireMock.StubFileContents("octo", "alpha", "A.txt", "alpha");
        factory.WireMock.StubFileContents("octo", "beta", "B.txt", "beta");

        using var client = factory.CreateAuthenticatedClient();
        await PostMcpAsync(client, BuildCall("octo", "alpha", "A.txt"));
        await PostMcpAsync(client, BuildCall("octo", "beta", "B.txt"));

        var tokenCalls = factory.WireMock
            .FindLogEntries(Request.Create()
                .WithPath("/app/installations/42/access_tokens")
                .UsingPost())
            .ToArray();

        tokenCalls.Should().HaveCount(2, "alpha and beta should mint independent repo-scoped IATs");

        var allRepos = new List<string>();
        foreach (var call in tokenCalls)
        {
            using var d = JsonDocument.Parse(call.RequestMessage.Body!);
            allRepos.AddRange(d.RootElement.GetProperty("repositories")
                .EnumerateArray().Select(e => e.GetString())!);
        }

        allRepos.Should().BeEquivalentTo(new[] { "alpha", "beta" });
    }

    [Fact]
    public async Task App_not_installed_404_returns_typed_mcp_error()
    {
        using var factory = new MultiRepoMcpFactory();
        factory.WireMock.StubInstallationDiscoveryNotFound("octo", "missing");

        using var client = factory.CreateAuthenticatedClient();
        var resp = await PostMcpAsync(client, BuildCall("octo", "missing", "README.md"));
        var body = await ReadMcpBodyAsync(resp);

        body.Should().Contain("not installed");
        body.Should().Contain("octo/missing", "the error mentions which owner/repo was tried");
    }

    [Fact]
    public async Task App_not_installed_422_at_mint_returns_typed_mcp_error()
    {
        using var factory = new MultiRepoMcpFactory();
        factory.WireMock.StubInstallationDiscovery("octo", "ghost", installationId: 77);
        factory.WireMock.StubTokenMint422(77);

        using var client = factory.CreateAuthenticatedClient();
        var resp = await PostMcpAsync(client, BuildCall("octo", "ghost", "README.md"));
        var body = await ReadMcpBodyAsync(resp);

        body.Should().Contain("not installed");
    }

    private static string BuildCall(string owner, string repo, string path) =>
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{"
        + "\"name\":\"get_file_contents\","
        + "\"arguments\":{\"owner\":\"" + owner + "\",\"repo\":\"" + repo + "\",\"path\":\"" + path + "\"}}}";

    private static async Task<HttpResponseMessage> PostMcpAsync(HttpClient client, string jsonBody)
    {
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return await client.PostAsync(new Uri("/mcp", UriKind.Relative), content);
    }

    /// <summary>
    /// Reads either a plain JSON body or extracts the first JSON event from a
    /// streamable-HTTP SSE response. The MCP HTTP transport may emit either
    /// shape depending on the negotiated content type.
    /// </summary>
    private static async Task<string> ReadMcpBodyAsync(HttpResponseMessage resp)
    {
        var raw = await resp.Content.ReadAsStringAsync();
        var mediaType = resp.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            // Find the first "data: {json}" line and return that JSON payload.
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
