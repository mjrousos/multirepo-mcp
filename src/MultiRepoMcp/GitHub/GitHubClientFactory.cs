using Microsoft.Extensions.Options;
using MultiRepoMcp.Configuration;
using Octokit;
using Octokit.Internal;

namespace MultiRepoMcp.GitHub;

/// <summary>
/// Backs <see cref="IGitHubClientFactory"/> with a single shared
/// <see cref="HttpClientAdapter"/> / <see cref="HttpMessageHandler"/> so that
/// per-request <see cref="GitHubClient"/> instances reuse the underlying
/// connection pool.
///
/// Only the wrapper objects (<see cref="Connection"/>, <see cref="GitHubClient"/>)
/// are allocated per request; the socket pool lives on the singleton handler.
/// </summary>
internal sealed class GitHubClientFactory : IGitHubClientFactory, IDisposable
{
    private static readonly ProductHeaderValue UserAgent = new("multirepo-mcp", "0.1.0");

    private readonly IGitHubAppJwtFactory _jwtFactory;
    private readonly Uri _apiBaseAddress;
    private readonly HttpClientAdapter _sharedAdapter;
    private readonly SocketsHttpHandler _ownedHandler;

    public GitHubClientFactory(
        IGitHubAppJwtFactory jwtFactory,
        IOptions<GitHubAppOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _jwtFactory = jwtFactory;
        _apiBaseAddress = options.Value.ApiBaseAddress ?? GitHubClient.GitHubApiUrl;

        _ownedHandler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            EnableMultipleHttp2Connections = true,
        };
        _sharedAdapter = new HttpClientAdapter(() => _ownedHandler);
    }

    public async ValueTask<IGitHubClient> CreateAppJwtClientAsync(CancellationToken cancellationToken)
    {
        var jwt = await _jwtFactory.CreateAsync(cancellationToken).ConfigureAwait(false);
        var connection = new Connection(
            UserAgent,
            _apiBaseAddress,
            new InMemoryCredentialStore(new Credentials(jwt, AuthenticationType.Bearer)),
            _sharedAdapter,
            new SimpleJsonSerializer());
        return new GitHubClient(connection);
    }

    public IGitHubClient CreateInstallationClient(string installationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationToken);

        var connection = new Connection(
            UserAgent,
            _apiBaseAddress,
            // Installation access tokens are sent as 'token <iat>' by Octokit
            // when AuthenticationType.Oauth is used, which is the form GitHub
            // expects for IATs.
            new InMemoryCredentialStore(new Credentials(installationToken, AuthenticationType.Oauth)),
            _sharedAdapter,
            new SimpleJsonSerializer());
        return new GitHubClient(connection);
    }

    /// <summary>Shared handler reference, exposed only for tests to assert reuse.</summary>
    internal HttpMessageHandler SharedHandler => _ownedHandler;

    public void Dispose()
    {
        _sharedAdapter.Dispose();
        _ownedHandler.Dispose();
    }
}
