using System.Text.Json;
using FluentAssertions;
using Moq;
using MultiRepoMcp.GitHub;
using MultiRepoMcp.Mcp.Tools;
using Octokit;

namespace MultiRepoMcp.UnitTests.Mcp;

/// <summary>
/// Unit tests for <see cref="GetFileContentsTool"/> directory-listing
/// behavior. The Octokit chain (Repository → Content → GetAllContents) is
/// mocked so we can craft mixed-type directory responses and assert the
/// shape, child <c>type</c> mapping, and ordering of the listing the tool
/// emits — coverage that the integration tests (file-only children) don't
/// exercise.
/// </summary>
public class GetFileContentsToolTests
{
    private const string Owner = "octo";
    private const string Repo = "hello";

    [Fact]
    public async Task Directory_listing_maps_entry_types_and_sorts_entries()
    {
        // Deliberately unordered and mixed-type to exercise both the
        // ContentType → string mapping and the (type, name) ordinal sort.
        var children = new[]
        {
            MakeContent("zebra.txt", "dir/zebra.txt", ContentType.File, size: 12),
            MakeContent("alpha", "dir/alpha", ContentType.Dir),
            MakeContent("link", "dir/link", ContentType.Symlink),
            MakeContent("mod", "dir/mod", ContentType.Submodule),
            MakeContent("apple.txt", "dir/apple.txt", ContentType.File, size: 7),
        };

        var result = await InvokeAsync("dir", children);
        using var doc = SerializeToDocument(result);
        var root = doc.RootElement;

        root.GetProperty("path").GetString().Should().Be("dir");
        root.GetProperty("type").GetString().Should().Be("dir");

        var entries = root.GetProperty("entries").EnumerateArray().ToArray();
        entries.Should().HaveCount(5);

        // Sorted by type (ordinal) then name: dir < file < submodule < symlink.
        var ordered = entries
            .Select(e => (Type: e.GetProperty("type").GetString(), Name: e.GetProperty("name").GetString()))
            .ToArray();
        ordered.Should().Equal(
            ("dir", "alpha"),
            ("file", "apple.txt"),
            ("file", "zebra.txt"),
            ("submodule", "mod"),
            ("symlink", "link"));
    }

    [Fact]
    public async Task Directory_listing_includes_metadata_fields_per_entry()
    {
        var children = new[]
        {
            MakeContent("readme.md", "docs/readme.md", ContentType.File, size: 42, sha: "deadbeef"),
        };

        var result = await InvokeAsync("docs", children);
        using var doc = SerializeToDocument(result);

        var entry = doc.RootElement.GetProperty("entries").EnumerateArray().Single();
        entry.GetProperty("name").GetString().Should().Be("readme.md");
        entry.GetProperty("path").GetString().Should().Be("docs/readme.md");
        entry.GetProperty("type").GetString().Should().Be("file");
        entry.GetProperty("size").GetInt32().Should().Be(42);
        entry.GetProperty("sha").GetString().Should().Be("deadbeef");
        entry.GetProperty("html_url").GetString().Should().Be("https://github.com/octo/hello/blob/main/docs/readme.md");
    }

    [Fact]
    public async Task Single_child_directory_is_returned_as_a_listing_not_a_file()
    {
        // GitHub returns a one-element array for a directory containing exactly
        // one child; the child's Path differs from the requested directory
        // path, which is how the tool distinguishes it from a file lookup.
        var children = new[]
        {
            MakeContent("only-child.txt", "lonely-dir/only-child.txt", ContentType.File, size: 3),
        };

        var result = await InvokeAsync("lonely-dir", children);
        using var doc = SerializeToDocument(result);
        var root = doc.RootElement;

        root.TryGetProperty("error", out _).Should().BeFalse("a directory listing is not an error response");
        root.GetProperty("type").GetString().Should().Be("dir");
        var entry = root.GetProperty("entries").EnumerateArray().Single();
        entry.GetProperty("path").GetString().Should().Be("lonely-dir/only-child.txt");
    }

    private static async Task<object> InvokeAsync(string requestedPath, IReadOnlyList<RepositoryContent> contents)
    {
        var contentsClient = new Mock<IRepositoryContentsClient>();
        contentsClient
            .Setup(c => c.GetAllContents(Owner, Repo, requestedPath))
            .ReturnsAsync(contents);

        var reposClient = new Mock<IRepositoriesClient>();
        reposClient.SetupGet(r => r.Content).Returns(contentsClient.Object);

        var githubClient = new Mock<IGitHubClient>();
        githubClient.SetupGet(c => c.Repository).Returns(reposClient.Object);

        var resolver = new Mock<IInstallationResolver>();
        resolver
            .Setup(r => r.ResolveAsync(Owner, Repo, It.IsAny<CancellationToken>()))
            .ReturnsAsync(99L);

        var tokenCache = new Mock<IInstallationTokenCache>();
        tokenCache
            .Setup(t => t.GetTokenAsync(99L, Owner, Repo, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CachedToken("ghs_test", DateTimeOffset.UtcNow.AddMinutes(60)));

        var clientFactory = new Mock<IGitHubClientFactory>();
        clientFactory
            .Setup(f => f.CreateInstallationClient(It.IsAny<string>()))
            .Returns(githubClient.Object);

        var tool = new GetFileContentsTool(resolver.Object, tokenCache.Object, clientFactory.Object);
        return await tool.GetFileContents(Owner, Repo, requestedPath);
    }

    private static RepositoryContent MakeContent(
        string name,
        string path,
        ContentType type,
        int size = 0,
        string sha = "sha")
        => new(
            name,
            path,
            sha,
            size,
            type,
            downloadUrl: $"https://example.test/raw/{path}",
            url: $"https://example.test/contents/{path}",
            gitUrl: $"https://example.test/git/blobs/{sha}",
            htmlUrl: $"https://github.com/{Owner}/{Repo}/blob/main/{path}",
            encoding: "base64",
            encodedContent: string.Empty,
            target: null!,
            submoduleGitUrl: null!);

    private static JsonDocument SerializeToDocument(object value)
        => JsonDocument.Parse(JsonSerializer.Serialize(value));
}
