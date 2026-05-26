using System.Net;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using MultiRepoMcp.GitHub.Exceptions;
using Octokit;

namespace MultiRepoMcp.Mcp;

/// <summary>
/// Maps internal/external exceptions to MCP-friendly errors.
///
/// MCP tool results travel as either a successful payload or a typed error
/// object returned by the tool. We surface errors via <see cref="BuildToolError"/>
/// (an object the tool returns) rather than by throwing — the underlying
/// AIFunction wrapper swallows raw exception messages for safety and the
/// caller would otherwise see a generic "An error occurred invoking ..."
/// string. <see cref="ToToolError"/> remains for unit-test convenience.
/// </summary>
internal static class McpToolErrorMapper
{
    /// <summary>Returns an <see cref="McpException"/> describing the supplied exception.</summary>
    public static McpException ToToolError(Exception exception, ILogger? logger = null)
    {
        var (_, message) = ClassifyForTool(exception, logger);
        return new McpException(message);
    }

    /// <summary>
    /// Returns a tool-result object (serialized as JSON to the MCP client) that
    /// surfaces the typed error class and a human-readable message. Use this
    /// instead of throwing inside a tool method.
    /// </summary>
    public static object BuildToolError(Exception exception, ILogger? logger = null)
    {
        var (kind, message) = ClassifyForTool(exception, logger);
        return new
        {
            error = kind,
            message,
        };
    }

    private static (string Kind, string Message) ClassifyForTool(Exception exception, ILogger? logger)
    {
        ArgumentNullException.ThrowIfNull(exception);

        switch (exception)
        {
            case AppNotInstalledException notInstalled:
                var owner = notInstalled.Owner ?? "<unknown>";
                var repo = notInstalled.Repo ?? "<unknown>";
                return ("AppNotInstalled",
                    $"The multirepo-mcp GitHub App is not installed on '{owner}/{repo}'. " +
                    "Install the App on the target repository and try again.");

            case NotFoundException:
                return ("NotFound", "File not found in the target repository.");

            case ArgumentException ae:
                return ("ValidationError", ae.Message);

            case RateLimitExceededException rate:
                return ("PrimaryRateLimit",
                    $"GitHub primary rate limit exceeded. Resets at {rate.Reset:O}.");

            case SecondaryRateLimitExceededException:
                return ("SecondaryRateLimit",
                    "GitHub secondary (abuse-detection) rate limit reached. " +
                    "Retry after the period indicated by GitHub's Retry-After header.");

            case ApiValidationException validation when validation.StatusCode == HttpStatusCode.UnprocessableEntity:
                return ("InvalidSearchQuery",
                    $"Invalid search query: {validation.Message}");

            case ApiException apiEx when apiEx.StatusCode == HttpStatusCode.UnprocessableEntity:
                return ("InvalidSearchQuery",
                    $"Invalid search query: {apiEx.Message}");

            case ApiException apiEx when apiEx.StatusCode == HttpStatusCode.Forbidden
                                      && apiEx.HttpResponse?.Headers is { } headers
                                      && headers.ContainsKey("Retry-After"):
                return ("SecondaryRateLimit",
                    "GitHub secondary (abuse-detection) rate limit reached. " +
                    "Retry after the period indicated by GitHub's Retry-After header.");

            case AuthorizationException:
                logger?.LogError(exception, "GitHub authentication failure.");
                return ("AuthFailure", "GitHub authentication failed. Please contact the server administrator.");

            case ApiException apiEx:
                logger?.LogWarning(exception, "Unhandled GitHub API exception (status={Status}).", apiEx.StatusCode);
                return ("GitHubApiError", $"GitHub API returned an error: {apiEx.Message}");

            default:
                logger?.LogError(exception, "Unhandled exception in MCP tool handler.");
                return ("InternalError", "An unexpected error occurred while contacting GitHub.");
        }
    }
}
