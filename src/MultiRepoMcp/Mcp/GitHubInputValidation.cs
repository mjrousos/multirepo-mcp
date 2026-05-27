using System.Text.RegularExpressions;

namespace MultiRepoMcp.Mcp;

/// <summary>
/// Shared validation for tool inputs (owner, repo, path, ref).
///
/// Validation throws <see cref="ArgumentException"/>; the error mapper turns
/// that into a user-friendly MCP error response.
/// </summary>
internal static partial class GitHubInputValidation
{
    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})?$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnerRegex();

    // GitHub repo names: letters/digits/hyphen/underscore/period, 1-100 chars,
    // cannot start/end with separator. We use the documented practical rules.
    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex RepoRegex();

    public static void ValidateOwnerRepo(string owner, string repo)
    {
        if (string.IsNullOrWhiteSpace(owner) || !OwnerRegex().IsMatch(owner))
        {
            throw new ArgumentException(
                $"Invalid GitHub owner '{owner}'. Expected 1–39 alphanumeric/hyphen characters " +
                "starting with an alphanumeric.",
                nameof(owner));
        }

        if (string.IsNullOrWhiteSpace(repo) || !RepoRegex().IsMatch(repo))
        {
            throw new ArgumentException(
                $"Invalid GitHub repository '{repo}'. Expected 1–100 characters from [A-Za-z0-9._-].",
                nameof(repo));
        }
    }

    public static void ValidatePath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("File path is required.", nameof(path));
        }

        if (path.Length > 1024)
        {
            throw new ArgumentException("File path exceeds 1024 characters.", nameof(path));
        }

        if (path.StartsWith('/'))
        {
            throw new ArgumentException("File path must not start with '/'.", nameof(path));
        }

        foreach (var c in path)
        {
            if (c == '\\')
            {
                throw new ArgumentException(
                    "File path must use forward slashes ('/'), not backslashes.",
                    nameof(path));
            }

            if (c == '\0' || char.IsControl(c))
            {
                throw new ArgumentException(
                    "File path contains control characters.",
                    nameof(path));
            }
        }
    }

    public static void ValidateRef(string? gitRef)
    {
        if (gitRef is null)
        {
            return; // Optional.
        }

        if (gitRef.Length == 0)
        {
            throw new ArgumentException("Git ref must not be empty when provided.", nameof(gitRef));
        }

        if (gitRef.Length > 250)
        {
            throw new ArgumentException("Git ref exceeds 250 characters.", nameof(gitRef));
        }

        foreach (var c in gitRef)
        {
            if (c == ' ' || c == '\0' || char.IsControl(c))
            {
                throw new ArgumentException(
                    "Git ref must not contain whitespace or control characters.",
                    nameof(gitRef));
            }
        }
    }
}
