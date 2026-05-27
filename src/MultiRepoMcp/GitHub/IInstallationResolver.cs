namespace MultiRepoMcp.GitHub;

/// <summary>
/// Resolves an <c>owner/repo</c> to the GitHub App installation that owns it
/// (positive results cached for <c>InstallationDiscoveryTtl</c>; negative
/// results cached for <c>InstallationNotFoundTtl</c>).
///
/// Throws <see cref="Exceptions.AppNotInstalledException"/> when GitHub
/// returns 404 (App not installed on the requested repo).
/// </summary>
internal interface IInstallationResolver
{
    ValueTask<long> ResolveAsync(string owner, string repo, CancellationToken cancellationToken);
}
