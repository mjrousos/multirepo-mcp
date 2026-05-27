namespace MultiRepoMcp.GitHub.Exceptions;

/// <summary>
/// Thrown when the GitHub App is not installed on the requested
/// <c>owner/repo</c>, either because installation discovery returned 404 or
/// because the installation-access-token mint returned 422 indicating the
/// installation cannot access the target repo.
/// </summary>
internal sealed class AppNotInstalledException : Exception
{
    public AppNotInstalledException()
        : this("GitHub App is not installed on the requested repository.")
    {
    }

    public AppNotInstalledException(string message)
        : base(message)
    {
    }

    public AppNotInstalledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AppNotInstalledException(string owner, string repo)
        : base($"GitHub App is not installed on '{owner}/{repo}'.")
    {
        Owner = owner;
        Repo = repo;
    }

    public AppNotInstalledException(string owner, string repo, Exception inner)
        : base($"GitHub App is not installed on '{owner}/{repo}'.", inner)
    {
        Owner = owner;
        Repo = repo;
    }

    public string? Owner { get; }
    public string? Repo { get; }
}
