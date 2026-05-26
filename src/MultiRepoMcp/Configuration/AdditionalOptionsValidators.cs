using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace MultiRepoMcp.Configuration;

internal sealed class GitHubAppOptionsValidator : IValidateOptions<GitHubAppOptions>
{
    public ValidateOptionsResult Validate(string? name, GitHubAppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.InstallationTokenRefreshThreshold <= TimeSpan.Zero
            || options.InstallationTokenRefreshThreshold >= TimeSpan.FromMinutes(60))
        {
            failures.Add(
                $"{nameof(GitHubAppOptions)}.{nameof(options.InstallationTokenRefreshThreshold)} must be > 0 and < 60 minutes.");
        }

        if (options.InstallationTokenRefreshJitter < TimeSpan.Zero
            || options.InstallationTokenRefreshJitter >= options.InstallationTokenRefreshThreshold)
        {
            failures.Add(
                $"{nameof(GitHubAppOptions)}.{nameof(options.InstallationTokenRefreshJitter)} must be >= 0 and " +
                $"strictly less than {nameof(options.InstallationTokenRefreshThreshold)}.");
        }

        if (options.KeyVaultUri is null)
        {
            // [Required] data annotation catches this; nothing else to add here.
        }
        else if (!options.KeyVaultUri.IsAbsoluteUri)
        {
            failures.Add($"{nameof(GitHubAppOptions)}.{nameof(options.KeyVaultUri)} must be an absolute URI.");
        }
        else if (options.KeyVaultUri.Scheme != Uri.UriSchemeHttps)
        {
            failures.Add($"{nameof(GitHubAppOptions)}.{nameof(options.KeyVaultUri)} must use the 'https' scheme.");
        }

        if (options.ApiBaseAddress is not null && !options.ApiBaseAddress.IsAbsoluteUri)
        {
            failures.Add($"{nameof(GitHubAppOptions)}.{nameof(options.ApiBaseAddress)} must be an absolute URI.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

internal sealed class CacheOptionsValidator : IValidateOptions<CacheOptions>
{
    public ValidateOptionsResult Validate(string? name, CacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.InstallationDiscoveryTtl <= TimeSpan.Zero)
        {
            failures.Add(
                $"{nameof(CacheOptions)}.{nameof(options.InstallationDiscoveryTtl)} must be > 0.");
        }

        if (options.InstallationNotFoundTtl <= TimeSpan.Zero)
        {
            failures.Add(
                $"{nameof(CacheOptions)}.{nameof(options.InstallationNotFoundTtl)} must be > 0.");
        }

        if (options.HealthCheckResultTtl <= TimeSpan.Zero)
        {
            failures.Add(
                $"{nameof(CacheOptions)}.{nameof(options.HealthCheckResultTtl)} must be > 0.");
        }

        if (options.HealthCheckDependencyTimeout <= TimeSpan.Zero
            || options.HealthCheckDependencyTimeout >= options.HealthCheckResultTtl)
        {
            failures.Add(
                $"{nameof(CacheOptions)}.{nameof(options.HealthCheckDependencyTimeout)} must be > 0 and " +
                $"strictly less than {nameof(options.HealthCheckResultTtl)}.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
