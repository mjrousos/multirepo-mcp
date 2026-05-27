using Octokit;

namespace MultiRepoMcp.GitHub;

/// <summary>
/// Constructs Octokit clients over a shared <see cref="HttpMessageHandler"/> /
/// <see cref="HttpClientAdapter"/> so we do not churn sockets per request.
/// </summary>
internal interface IGitHubClientFactory
{
    /// <summary>
    /// Creates a GitHub client authenticated as the App itself (JWT bearer).
    /// Used by <see cref="IInstallationResolver"/> and the GitHub health check.
    /// </summary>
    ValueTask<IGitHubClient> CreateAppJwtClientAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates a GitHub client authenticated with the supplied installation
    /// access token (IAT).
    /// </summary>
    IGitHubClient CreateInstallationClient(string installationToken);
}
