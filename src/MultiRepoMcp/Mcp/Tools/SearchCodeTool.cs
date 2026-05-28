using System.ComponentModel;
using ModelContextProtocol.Server;
using MultiRepoMcp.GitHub;
using Octokit;

namespace MultiRepoMcp.Mcp.Tools;

[McpServerToolType]
internal sealed class SearchCodeTool
{
    public const int DefaultMaxResults = 30;
    public const int AbsoluteMaxResults = 100;

    private readonly IInstallationResolver _resolver;
    private readonly IInstallationTokenCache _tokenCache;
    private readonly IGitHubClientFactory _clientFactory;

    public SearchCodeTool(
        IInstallationResolver resolver,
        IInstallationTokenCache tokenCache,
        IGitHubClientFactory clientFactory)
    {
        _resolver = resolver;
        _tokenCache = tokenCache;
        _clientFactory = clientFactory;
    }

    [McpServerTool(Name = "search_code")]
    [Description(
        "Search code within a single GitHub repository using GitHub's code-search REST API. " +
        "Multiple terms are implicitly AND-joined (all terms must appear in the same file). " +
        "Use \"quoted phrases\" for exact multi-word matches. " +
        "Limitations: only files on the repository's DEFAULT BRANCH are searched, files larger " +
        "than ~384 KB are not indexed, and the repository must be indexed by GitHub's code-search " +
        "(generally automatic for org-owned repos; personal repos may need opt-in). " +
        "Queries cannot include scope qualifiers (repo:/path:/etc.), boolean operators (OR/NOT), " +
        "or leading-dash exclusions — those are reserved by the tool's per-repo scoping.")]
    public async Task<object> SearchCode(
        [Description("Repository owner (user or org).")] string owner,
        [Description("Repository name.")] string repo,
        [Description("Free-text search expression. Multiple terms are implicitly AND-joined; use double quotes for exact phrases or literals containing ':' or '-'. No qualifiers, OR/NOT, or leading-dash exclusions.")] string query,
        [Description("Maximum number of results to return. Defaults to 30; capped at 100.")] int? maxResults = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            GitHubInputValidation.ValidateOwnerRepo(owner, repo);
            SearchCodeQueryValidation.Validate(query);

            var cap = Math.Clamp(maxResults ?? DefaultMaxResults, 1, AbsoluteMaxResults);

            var installationId = await _resolver.ResolveAsync(owner, repo, cancellationToken).ConfigureAwait(false);
            var token = await _tokenCache.GetTokenAsync(installationId, owner, repo, cancellationToken).ConfigureAwait(false);
            var client = _clientFactory.CreateInstallationClient(token.Token);

            var request = new SearchCodeRequest(query)
            {
                Repos = new RepositoryCollection { { owner, repo } },
                PerPage = cap,
            };

            var result = await client.Search.SearchCode(request).ConfigureAwait(false);

            var hits = result.Items
                .Take(cap)
                .Select(item => new
                {
                    path = item.Path,
                    name = item.Name,
                    sha = item.Sha,
                    html_url = item.HtmlUrl,
                    repo = $"{owner}/{repo}",
                })
                .ToArray();

            return new
            {
                total_count = result.TotalCount,
                incomplete_results = result.IncompleteResults,
                returned_count = hits.Length,
                results = hits,
                notes = result.TotalCount == 0
                    ? "No matches. If the repository is not indexed by GitHub code-search, results will always be empty. " +
                      "As a fallback when you know the file path, use the get_file_contents tool to fetch (or list a directory and then fetch) the file directly."
                    : null,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return McpToolErrorMapper.BuildToolError(ex);
        }
    }
}
