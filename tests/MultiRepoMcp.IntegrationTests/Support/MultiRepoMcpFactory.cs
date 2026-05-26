using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MultiRepoMcp;
using WireMock.Server;

namespace MultiRepoMcp.IntegrationTests.Support;

/// <summary>
/// Hosts the MultiRepoMcp server in an in-memory test server, pointing GitHub
/// API traffic at a WireMock.Net instance and bypassing Key Vault via a
/// locally-generated PEM file.
/// </summary>
public sealed class MultiRepoMcpFactory : WebApplicationFactory<Program>
{
    public const string BearerToken = "integration-test-bearer-token-1234567890";

    private readonly WireMockServer _wireMock;
    private readonly string _pemPath;

    public IReadOnlyList<string>? CallerAllowlist { get; set; }
    public string AllowedHosts { get; set; } = "*";

    public MultiRepoMcpFactory()
    {
        _wireMock = WireMockServer.Start();
        _pemPath = WriteTempPem();
    }

    public WireMockServer WireMock => _wireMock;
    public string WireMockUrl => _wireMock.Urls[0];

    /// <summary>Configured client preset with the test bearer + a sample caller header.</summary>
    public HttpClient CreateAuthenticatedClient(string callerRepo = "octo/cat")
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", BearerToken);
        client.DefaultRequestHeaders.Add("X-Caller-Repository", callerRepo);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            var overrides = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["AllowedHosts"] = AllowedHosts,
                ["Authentication:BearerToken"] = BearerToken,
                ["GitHubApp:AppId"] = "12345",
                ["GitHubApp:KeyVaultUri"] = "https://example.vault.azure.net/",
                ["GitHubApp:PrivateKeySecretName"] = "test-secret",
                ["GitHubApp:LocalPrivateKeyPath"] = _pemPath,
                ["GitHubApp:ApiBaseAddress"] = WireMockUrl + "/",
                // Tight cache TTLs keep tests deterministic; the health-check
                // caching test overrides this via its own factory instance.
                ["Cache:HealthCheckResultTtl"] = "00:00:30",
                ["Cache:HealthCheckDependencyTimeout"] = "00:00:03",
                ["Cache:InstallationDiscoveryTtl"] = "01:00:00",
            };

            if (CallerAllowlist is { Count: > 0 })
            {
                for (var i = 0; i < CallerAllowlist.Count; i++)
                {
                    overrides[$"Authentication:CallerRepositoryAllowlist:{i}"] = CallerAllowlist[i];
                }
            }

            config.AddInMemoryCollection(overrides!);
        });

        builder.ConfigureTestServices(_ =>
        {
            // No service replacements needed — the LocalPrivateKeyPath /
            // ApiBaseAddress overrides above are sufficient to point the
            // existing code at our test doubles.
        });
    }

    public static string GenerateTokenExpiringIn(TimeSpan lifetime) =>
        "ghs_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace('+', '_')
            .Replace('/', '-')
            .TrimEnd('=');

    public static string GitHubExpiresAtString(DateTimeOffset when) =>
        when.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _wireMock.Stop();
            _wireMock.Dispose();
            try
            {
                File.Delete(_pemPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup; the OS will reclaim the temp file.
            }
        }
    }

    private static string WriteTempPem()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        var path = Path.Combine(Path.GetTempPath(), $"multirepo-mcp-test-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, pem);
        return path;
    }
}
