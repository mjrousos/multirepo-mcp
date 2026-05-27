using System.Net;
using FluentAssertions;
using ModelContextProtocol;
using MultiRepoMcp.GitHub.Exceptions;
using MultiRepoMcp.Mcp;
using Octokit;

namespace MultiRepoMcp.UnitTests.Mcp;

public class McpToolErrorMapperTests
{
    [Fact]
    public void AppNotInstalled_includes_owner_and_repo()
    {
        var err = McpToolErrorMapper.ToToolError(new AppNotInstalledException("octo", "hello"));
        err.Should().BeOfType<McpException>();
        err.Message.Should().Contain("octo/hello").And.Contain("not installed");
    }

    [Fact]
    public void NotFound_yields_File_not_found()
    {
        var err = McpToolErrorMapper.ToToolError(
            new NotFoundException("missing", HttpStatusCode.NotFound));
        err.Message.Should().Contain("File not found");
    }

    [Fact]
    public void ArgumentException_passes_through_message()
    {
        var err = McpToolErrorMapper.ToToolError(new ArgumentException("bad path"));
        err.Message.Should().Contain("bad path");
    }

    [Fact]
    public void Primary_rate_limit_distinct_from_secondary()
    {
        var primary = McpToolErrorMapper.ToToolError(
            new RateLimitExceededException(MakeResponse(HttpStatusCode.Forbidden)));
        primary.Message.Should().Contain("primary rate limit");

        var secondary = McpToolErrorMapper.ToToolError(
            new SecondaryRateLimitExceededException(MakeResponse(HttpStatusCode.Forbidden)));
        secondary.Message.Should().Contain("secondary").And.NotContainEquivalentOf("primary");
    }

    [Fact]
    public void ApiValidationException_422_maps_to_invalid_search_query()
    {
        var ex = new ApiValidationException();
        var err = McpToolErrorMapper.ToToolError(ex);
        err.Message.Should().Contain("Invalid search query");
    }

    [Fact]
    public void AuthorizationException_yields_generic_auth_failure_message()
    {
        var err = McpToolErrorMapper.ToToolError(new AuthorizationException());
        err.Message.Should().Contain("authentication failed");
    }

    [Fact]
    public void Generic_exception_yields_generic_message()
    {
        var err = McpToolErrorMapper.ToToolError(new InvalidOperationException("oh no"));
        err.Message.Should().Contain("unexpected error");
    }

    private static IResponse MakeResponse(HttpStatusCode status) => new FakeResponse(status);

    private sealed class FakeResponse : IResponse
    {
        public FakeResponse(HttpStatusCode status) => StatusCode = status;
        public object? Body => string.Empty;
        public IReadOnlyDictionary<string, string> Headers { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public ApiInfo ApiInfo { get; } = new(
            new Dictionary<string, Uri>(StringComparer.Ordinal),
            new List<string>(),
            new List<string>(),
            "etag",
            new RateLimit(new Dictionary<string, string>(StringComparer.Ordinal)),
            TimeSpan.Zero);
        public HttpStatusCode StatusCode { get; }
        public string ContentType => "application/json";
    }
}
