using System.Text.RegularExpressions;

namespace MultiRepoMcp.Mcp;

/// <summary>
/// Defense-in-depth rejection of <c>search_code</c> queries that try to
/// escape the per-repo scope, use boolean operators, or use leading-dash
/// exclusion. The primary defense is the repo-scoped IAT (the token cannot
/// see other repos); this validation simply gives callers a clear error.
/// </summary>
internal static partial class SearchCodeQueryValidation
{
    // Any token that LOOKS like a GitHub search qualifier: lower-case word + ':'.
    [GeneratedRegex(@"\b[a-z][a-z0-9_-]*:", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex QualifierRegex();

    public static void Validate(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new ArgumentException("Search query is required.", nameof(query));
        }

        if (query.Length > 1024)
        {
            throw new ArgumentException("Search query exceeds 1024 characters.", nameof(query));
        }

        var (sanitized, doubleQuotedRanges) = ExtractDoubleQuotedRanges(query);
        _ = doubleQuotedRanges; // Use the sanitized string for matching.

        foreach (Match match in QualifierRegex().Matches(sanitized))
        {
            throw new ArgumentException(
                $"search_code queries cannot include scope qualifiers (found '{match.Value}'). " +
                "The repository scope is set by the tool's `owner`/`repo` parameters; " +
                "use double quotes for literal terms containing ':' or '-'.",
                nameof(query));
        }

        // Boolean operators / negation, case-sensitive (the GitHub DSL is).
        foreach (var token in sanitized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token is "OR" or "NOT")
            {
                throw new ArgumentException(
                    $"search_code queries cannot include boolean operators (found '{token}'). " +
                    "Use double quotes for literal terms containing 'OR' or 'NOT'.",
                    nameof(query));
            }

            if (token.StartsWith('-') && token.Length > 1)
            {
                throw new ArgumentException(
                    "search_code queries cannot include leading-dash exclusions. " +
                    "Use double quotes for literal terms beginning with '-'.",
                    nameof(query));
            }
        }
    }

    /// <summary>
    /// Replaces double-quoted literal runs in the query with spaces so the
    /// qualifier/boolean-operator regexes don't false-positive on them.
    /// Returns the sanitized string plus the original ranges (unused at the
    /// moment but kept for future test diagnostics).
    /// </summary>
    private static (string Sanitized, IReadOnlyList<(int Start, int End)> QuotedRanges) ExtractDoubleQuotedRanges(string query)
    {
        Span<char> buffer = stackalloc char[query.Length];
        query.AsSpan().CopyTo(buffer);
        var ranges = new List<(int Start, int End)>();

        bool insideQuotes = false;
        int quoteStart = -1;
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] == '"')
            {
                if (!insideQuotes)
                {
                    insideQuotes = true;
                    quoteStart = i;
                }
                else
                {
                    ranges.Add((quoteStart, i));
                    for (int j = quoteStart; j <= i; j++)
                    {
                        buffer[j] = ' ';
                    }
                    insideQuotes = false;
                    quoteStart = -1;
                }
            }
        }

        return (buffer.ToString(), ranges);
    }
}
