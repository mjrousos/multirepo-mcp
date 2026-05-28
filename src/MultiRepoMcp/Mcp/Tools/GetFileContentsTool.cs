using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using MultiRepoMcp.GitHub;
using Octokit;

namespace MultiRepoMcp.Mcp.Tools;

[McpServerToolType]
internal sealed class GetFileContentsTool
{
    /// <summary>Maximum file size (in bytes) we will return content for. ~1 MiB.</summary>
    public const int MaxFileSize = 1 * 1024 * 1024;

    private readonly IInstallationResolver _resolver;
    private readonly IInstallationTokenCache _tokenCache;
    private readonly IGitHubClientFactory _clientFactory;

    public GetFileContentsTool(
        IInstallationResolver resolver,
        IInstallationTokenCache tokenCache,
        IGitHubClientFactory clientFactory)
    {
        _resolver = resolver;
        _tokenCache = tokenCache;
        _clientFactory = clientFactory;
    }

    [McpServerTool(Name = "get_file_contents")]
    [Description(
        "Read the contents of a file or directory from a GitHub repository. " +
        "For a file, returns the content (text or base64-encoded for binary), size, and sha. " +
        "For a directory, returns a listing of its immediate child entries. " +
        "Returns typed errors for submodules, symlinks, and files larger than 1 MiB.")]
    public async Task<object> GetFileContents(
        [Description("Repository owner (user or org).")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Path to a file or directory from the repository root (e.g. 'src/Program.cs' or 'src').")] string path,
        [Description("Optional ref (branch, tag, or commit SHA). Defaults to the repository's default branch.")] string? @ref = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            GitHubInputValidation.ValidateOwnerRepo(owner, repo);
            GitHubInputValidation.ValidatePath(path);
            GitHubInputValidation.ValidateRef(@ref);

            var installationId = await _resolver.ResolveAsync(owner, repo, cancellationToken).ConfigureAwait(false);
            var token = await _tokenCache.GetTokenAsync(installationId, owner, repo, cancellationToken).ConfigureAwait(false);
            var client = _clientFactory.CreateInstallationClient(token.Token);

            IReadOnlyList<RepositoryContent> contents;
            try
            {
                contents = string.IsNullOrEmpty(@ref)
                    ? await client.Repository.Content.GetAllContents(owner, repo, path).ConfigureAwait(false)
                    : await client.Repository.Content.GetAllContentsByRef(owner, repo, path, @ref).ConfigureAwait(false);
            }
            catch (NotFoundException)
            {
                throw new NotFoundException("File not found.", System.Net.HttpStatusCode.NotFound);
            }

            if (contents is null || contents.Count == 0)
            {
                throw new NotFoundException("File not found.", System.Net.HttpStatusCode.NotFound);
            }

            // Directory detection: Octokit returns a List for both file lookups
            // and directory listings. For a file/symlink/submodule, the single
            // entry's Path equals the requested path. For a directory, each
            // entry's Path is a child under the requested prefix — including
            // the corner case where the directory has exactly one child, which
            // count-based detection would mis-classify as a file.
            var entry = contents[0];
            var entryPath = entry.Path?.TrimStart('/') ?? string.Empty;
            var requestedPath = path.TrimStart('/');
            var isDirectoryListing = contents.Count > 1
                || !string.Equals(entryPath, requestedPath, StringComparison.Ordinal);
            if (isDirectoryListing)
            {
                return BuildDirectoryListing(path, contents);
            }

            switch (entry.Type.Value)
            {
                case ContentType.Dir:
                    // Defensive: an entry whose own Path matches the request but
                    // is typed as a directory (e.g. an empty directory) is still
                    // returned as a — possibly empty — listing.
                    return BuildDirectoryListing(path, contents);

                case ContentType.Submodule:
                    return new
                    {
                        error = "PathIsSubmodule",
                        message = "Path resolves to a submodule; fetch from the submodule's source repo separately.",
                        path,
                        submodule_git_url = entry.SubmoduleGitUrl,
                        sha = entry.Sha,
                    };

                case ContentType.Symlink:
                    return new
                    {
                        error = "PathIsSymlink",
                        message = "Path resolves to a symlink; not auto-followed in this version. Re-invoke with the resolved target.",
                        path,
                        target = entry.Target,
                        sha = entry.Sha,
                    };

                case ContentType.File:
                default:
                    // Size check happens BEFORE base64 decode of `entry.Content`.
                    if (entry.Size > MaxFileSize)
                    {
                        return new
                        {
                            error = "FileTooLarge",
                            message = $"File size ({entry.Size} bytes) exceeds the {MaxFileSize}-byte cap.",
                            path,
                            size = entry.Size,
                            sha = entry.Sha,
                        };
                    }

                    var (text, isBinary) = TryDecode(entry);
                    return new
                    {
                        path = entry.Path,
                        sha = entry.Sha,
                        size = entry.Size,
                        html_url = entry.HtmlUrl,
                        encoding = isBinary ? "base64" : "utf-8",
                        content = text,
                    };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return McpToolErrorMapper.BuildToolError(ex);
        }
    }

    private static object BuildDirectoryListing(string path, IReadOnlyList<RepositoryContent> contents)
    {
        var entries = contents
            .Select(c => new
            {
                name = c.Name,
                path = c.Path,
                type = MapContentType(c.Type.Value),
                size = c.Size,
                sha = c.Sha,
                html_url = c.HtmlUrl,
            })
            .OrderBy(e => e.type, StringComparer.Ordinal)
            .ThenBy(e => e.name, StringComparer.Ordinal)
            .ToArray();

        return new
        {
            path,
            type = "dir",
            entries,
        };
    }

    private static string MapContentType(ContentType type) => type switch
    {
        ContentType.Dir => "dir",
        ContentType.Submodule => "submodule",
        ContentType.Symlink => "symlink",
        _ => "file",
    };

    private static (string Content, bool IsBinary) TryDecode(RepositoryContent entry)
    {
        // Octokit decodes `content` when the response is text-able, but raw bytes
        // are exposed via `EncodedContent`. Prefer the decoded `Content` when
        // present, falling back to the base64 envelope otherwise.
        if (!string.IsNullOrEmpty(entry.Content))
        {
            return (entry.Content, IsBinary: false);
        }

        if (!string.IsNullOrEmpty(entry.EncodedContent))
        {
            // Try to round-trip as UTF-8; if it fails or contains nulls/controls
            // we'll treat the file as binary and return base64.
            try
            {
                var raw = Convert.FromBase64String(entry.EncodedContent);
                if (LooksLikeText(raw))
                {
                    return (Encoding.UTF8.GetString(raw), IsBinary: false);
                }

                return (entry.EncodedContent, IsBinary: true);
            }
            catch (FormatException)
            {
                return (entry.EncodedContent, IsBinary: true);
            }
        }

        return (string.Empty, IsBinary: false);
    }

    private static bool LooksLikeText(byte[] raw)
    {
        // Cheap heuristic: no NUL bytes and < 5% control chars.
        if (raw.Length == 0)
        {
            return true;
        }

        int controlCount = 0;
        foreach (var b in raw)
        {
            if (b == 0)
            {
                return false;
            }

            if (b < 0x20 && b != (byte)'\n' && b != (byte)'\r' && b != (byte)'\t')
            {
                controlCount++;
            }
        }

        return controlCount * 20 < raw.Length;
    }
}
