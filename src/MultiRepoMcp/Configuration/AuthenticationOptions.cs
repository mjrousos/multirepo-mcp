using System.ComponentModel.DataAnnotations;

namespace MultiRepoMcp.Configuration;

public sealed class AuthenticationOptions
{
    public const string SectionName = "Authentication";

    /// <summary>
    /// Static bearer token that callers must present in the
    /// <c>Authorization: Bearer ...</c> header. Validated at startup and
    /// compared in constant time at request time.
    /// </summary>
    [Required]
    [MinLength(20)]
    public string BearerToken { get; init; } = string.Empty;

    /// <summary>
    /// Optional list of <c>owner/repo</c> values allowed to call the server,
    /// presented by callers in the <c>X-Caller-Repository</c> header.
    /// When null or empty, the allowlist check is disabled.
    /// </summary>
    public IReadOnlyList<string>? CallerRepositoryAllowlist { get; init; }
}
