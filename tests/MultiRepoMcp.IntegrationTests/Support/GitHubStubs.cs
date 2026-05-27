using System.Net;
using System.Text;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace MultiRepoMcp.IntegrationTests.Support;

/// <summary>
/// Convenience helpers for stubbing the GitHub REST endpoints we depend on.
/// All stubs use a permissive JSON content type and minimal-but-valid payloads.
/// </summary>
internal static class GitHubStubs
{
    public static void StubAppMetadata(this WireMockServer wireMock)
    {
        wireMock.Given(Request.Create().WithPath("/app").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.OK)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"id":12345,"slug":"multirepo-mcp","name":"multirepo-mcp"}"""));
    }

    public static void StubInstallationDiscovery(
        this WireMockServer wireMock,
        string owner,
        string repo,
        long installationId)
    {
        var body = "{\"id\":" + installationId + ",\"account\":{\"login\":\"" + owner + "\"}}";
        wireMock.Given(Request.Create()
                .WithPath($"/repos/{owner}/{repo}/installation")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.OK)
                .WithHeader("Content-Type", "application/json")
                .WithBody(body));
    }

    public static void StubInstallationDiscoveryNotFound(
        this WireMockServer wireMock,
        string owner,
        string repo)
    {
        wireMock.Given(Request.Create()
                .WithPath($"/repos/{owner}/{repo}/installation")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.NotFound)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"message":"Not Found","documentation_url":""}"""));
    }

    public static void StubTokenMint(
        this WireMockServer wireMock,
        long installationId,
        string token,
        DateTimeOffset expiresAt)
    {
        var body = "{\"token\":\"" + token + "\",\"expires_at\":\""
                   + MultiRepoMcpFactory.GitHubExpiresAtString(expiresAt) + "\"}";
        wireMock.Given(Request.Create()
                .WithPath($"/app/installations/{installationId}/access_tokens")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.Created)
                .WithHeader("Content-Type", "application/json")
                .WithBody(body));
    }

    public static void StubTokenMint422(
        this WireMockServer wireMock,
        long installationId)
    {
        wireMock.Given(Request.Create()
                .WithPath($"/app/installations/{installationId}/access_tokens")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.UnprocessableEntity)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"message":"There is at least one repository that does not exist or is not accessible to the parent installation."}"""));
    }

    public static void StubFileContents(
        this WireMockServer wireMock,
        string owner,
        string repo,
        string path,
        string content,
        string? sha = null)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        var fileSha = sha ?? "abc123";
        var size = Encoding.UTF8.GetByteCount(content);
        var name = Path.GetFileName(path);
        var wmUrl = wireMock.Urls[0];
        var sb = new StringBuilder();
        sb.Append('{');
        sb.Append("\"name\":\"").Append(name).Append("\",");
        sb.Append("\"path\":\"").Append(path).Append("\",");
        sb.Append("\"sha\":\"").Append(fileSha).Append("\",");
        sb.Append("\"size\":").Append(size).Append(',');
        sb.Append("\"url\":\"").Append(wmUrl).Append("/repos/").Append(owner).Append('/').Append(repo).Append("/contents/").Append(path).Append("\",");
        sb.Append("\"html_url\":\"https://github.com/").Append(owner).Append('/').Append(repo).Append("/blob/main/").Append(path).Append("\",");
        sb.Append("\"git_url\":\"").Append(wmUrl).Append("/repos/").Append(owner).Append('/').Append(repo).Append("/git/blobs/").Append(fileSha).Append("\",");
        sb.Append("\"download_url\":\"").Append(wmUrl).Append("/raw/").Append(owner).Append('/').Append(repo).Append("/main/").Append(path).Append("\",");
        sb.Append("\"type\":\"file\",");
        sb.Append("\"content\":\"").Append(base64).Append("\",");
        sb.Append("\"encoding\":\"base64\"");
        sb.Append('}');

        wireMock.Given(Request.Create()
                .WithPath($"/repos/{owner}/{repo}/contents/{path}")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.OK)
                .WithHeader("Content-Type", "application/json")
                .WithBody(sb.ToString()));
    }

    /// <summary>
    /// Stubs a GitHub directory-listing response: a JSON ARRAY of child
    /// entries under <paramref name="dirPath"/>. GitHub returns the listing
    /// shape for both multi-child and single-child directories, which makes
    /// count-based "is this a directory" detection ambiguous — the server
    /// must look at the entries' Paths.
    /// </summary>
    public static void StubDirectoryListing(
        this WireMockServer wireMock,
        string owner,
        string repo,
        string dirPath,
        params string[] childNames)
    {
        var wmUrl = wireMock.Urls[0];
        var sb = new StringBuilder();
        sb.Append('[');
        for (var i = 0; i < childNames.Length; i++)
        {
            if (i > 0) sb.Append(',');
            var child = childNames[i];
            var childPath = $"{dirPath}/{child}";
            sb.Append('{');
            sb.Append("\"name\":\"").Append(child).Append("\",");
            sb.Append("\"path\":\"").Append(childPath).Append("\",");
            sb.Append("\"sha\":\"sha-").Append(i).Append("\",");
            sb.Append("\"size\":0,");
            sb.Append("\"url\":\"").Append(wmUrl).Append("/repos/").Append(owner).Append('/').Append(repo).Append("/contents/").Append(childPath).Append("\",");
            sb.Append("\"html_url\":\"https://github.com/").Append(owner).Append('/').Append(repo).Append("/blob/main/").Append(childPath).Append("\",");
            sb.Append("\"git_url\":\"").Append(wmUrl).Append("/repos/").Append(owner).Append('/').Append(repo).Append("/git/blobs/sha-").Append(i).Append("\",");
            sb.Append("\"download_url\":\"").Append(wmUrl).Append("/raw/").Append(owner).Append('/').Append(repo).Append("/main/").Append(childPath).Append("\",");
            sb.Append("\"type\":\"file\",");
            sb.Append("\"content\":\"\",");
            sb.Append("\"encoding\":\"base64\"");
            sb.Append('}');
        }
        sb.Append(']');

        wireMock.Given(Request.Create()
                .WithPath($"/repos/{owner}/{repo}/contents/{dirPath}")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.OK)
                .WithHeader("Content-Type", "application/json")
                .WithBody(sb.ToString()));
    }

    public static void StubSearchCode(
        this WireMockServer wireMock,
        string owner,
        string repo)
    {
        _ = owner; _ = repo;
        // Minimal valid response: zero items so Octokit's deep Repository-model
        // deserialization never runs. We assert the OUTBOUND query (which is the
        // security-relevant signal) via the WireMock request log instead.
        wireMock.Given(Request.Create()
                .WithPath("/search/code")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(HttpStatusCode.OK)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"total_count":0,"incomplete_results":false,"items":[]}"""));
    }
}
