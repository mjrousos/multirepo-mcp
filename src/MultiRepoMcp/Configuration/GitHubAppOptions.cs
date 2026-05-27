using System.ComponentModel.DataAnnotations;

namespace MultiRepoMcp.Configuration;

public sealed class GitHubAppOptions
{
    public const string SectionName = "GitHubApp";

    /// <summary>The numeric GitHub App ID (used as the JWT <c>iss</c> claim).</summary>
    [Range(1, long.MaxValue)]
    public long AppId { get; init; }

    /// <summary>Azure Key Vault URI hosting the App's PEM private key.</summary>
    [Required]
    public Uri? KeyVaultUri { get; init; }

    /// <summary>Name of the Key Vault <b>key</b> (not secret) used to sign App JWTs. The
    /// private key never leaves Key Vault — this process only ever sees the
    /// signature output. Defaults to <c>multirepo-mcp-private-key</c> so a minimal
    /// configuration works when the operator follows the README naming convention.</summary>
    [Required]
    [MinLength(1)]
    public string PrivateKeyName { get; init; } = "multirepo-mcp-private-key";

    /// <summary>
    /// Proactively refresh installation access tokens when their remaining
    /// lifetime falls below this threshold. Default 5 minutes.
    /// </summary>
    public TimeSpan InstallationTokenRefreshThreshold { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum per-key jitter added to the refresh threshold to spread
    /// near-simultaneous refreshes across many installations.
    /// </summary>
    public TimeSpan InstallationTokenRefreshJitter { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Optional override of the GitHub REST API base address (used by tests
    /// to redirect Octokit traffic to WireMock.Net).
    /// </summary>
    public Uri? ApiBaseAddress { get; init; }

    /// <summary>
    /// Whether the App is configured to allow loading the PEM from a local
    /// file rather than Key Vault. Intended for development only.
    /// </summary>
    public string? LocalPrivateKeyPath { get; init; }
}
