using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;

namespace MultiRepoMcp.Configuration;

/// <summary>
/// Strict startup validation for <see cref="AuthenticationOptions"/> that goes
/// beyond data annotations: rejects obvious placeholder bearer-token values so
/// a half-finished config cannot accidentally accept an attacker-guessable token.
/// </summary>
internal sealed class AuthenticationOptionsValidator : IValidateOptions<AuthenticationOptions>
{
    private static readonly HashSet<string> PlaceholderValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "...",
        "changeme",
        "change-me",
        "todo",
        "replace-me",
        "your-token-here",
        "your-bearer-token",
        "secret",
        "token",
    };

    public ValidateOptionsResult Validate(string? name, AuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        // Data annotations run separately via ValidateDataAnnotations(); we focus on the
        // semantic checks data annotations cannot express.
        var token = options.BearerToken;

        if (!string.IsNullOrEmpty(token))
        {
            if (PlaceholderValues.Contains(token))
            {
                failures.Add(
                    $"{nameof(AuthenticationOptions)}.{nameof(options.BearerToken)} contains a known placeholder value " +
                    "('changeme'/'replace-me'/etc.). Configure a real bearer secret.");
            }
            else if (IsAllSameCharacter(token))
            {
                failures.Add(
                    $"{nameof(AuthenticationOptions)}.{nameof(options.BearerToken)} contains only repeated characters " +
                    "(e.g. 'aaaaaaaaaaaaaaaaaaaa'). Configure a real bearer secret.");
            }
        }

        if (options.CallerRepositoryAllowlist is { Count: > 0 } allowlist)
        {
            foreach (var entry in allowlist)
            {
                if (!LooksLikeOwnerRepo(entry))
                {
                    failures.Add(
                        $"{nameof(AuthenticationOptions)}.{nameof(options.CallerRepositoryAllowlist)} entry '{entry}' " +
                        "is not a valid 'owner/repo' value.");
                }
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    [SuppressMessage("Performance", "CA1865:Use char overloads", Justification = "String comparand needs no allocation; clarity wins.")]
    private static bool IsAllSameCharacter(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        var first = value[0];
        for (var i = 1; i < value.Length; i++)
        {
            if (value[i] != first)
            {
                return false;
            }
        }

        return true;
    }

    private static bool LooksLikeOwnerRepo(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('/');
        return parts.Length == 2
            && !string.IsNullOrWhiteSpace(parts[0])
            && !string.IsNullOrWhiteSpace(parts[1]);
    }
}
