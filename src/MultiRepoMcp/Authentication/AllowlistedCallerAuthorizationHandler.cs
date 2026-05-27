using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using MultiRepoMcp.Configuration;

namespace MultiRepoMcp.Authentication;

/// <summary>
/// Marker policy requirement that the caller's self-reported
/// <c>X-Caller-Repository</c> header is present and (when an allowlist is
/// configured) listed in <see cref="AuthenticationOptions.CallerRepositoryAllowlist"/>.
/// </summary>
internal sealed class AllowlistedCallerRequirement : IAuthorizationRequirement
{
    public const string PolicyName = "RequireAllowlistedCaller";
    public const string HeaderName = "X-Caller-Repository";
}

internal sealed class AllowlistedCallerAuthorizationHandler
    : AuthorizationHandler<AllowlistedCallerRequirement>
{
    private readonly IOptionsMonitor<AuthenticationOptions> _authOptions;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<AllowlistedCallerAuthorizationHandler> _logger;

    public AllowlistedCallerAuthorizationHandler(
        IOptionsMonitor<AuthenticationOptions> authOptions,
        IHttpContextAccessor httpContextAccessor,
        ILogger<AllowlistedCallerAuthorizationHandler> logger)
    {
        _authOptions = authOptions;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AllowlistedCallerRequirement requirement)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            // No HTTP context (e.g., background processing) — succeed silently;
            // the auth pipeline isn't applicable.
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var allowlist = _authOptions.CurrentValue.CallerRepositoryAllowlist;
        var allowlistEnabled = allowlist is { Count: > 0 };

        if (!httpContext.Request.Headers.TryGetValue(
                AllowlistedCallerRequirement.HeaderName, out var headerValues)
            || headerValues.Count == 0
            || string.IsNullOrWhiteSpace(headerValues[0]))
        {
            if (allowlistEnabled)
            {
                _logger.LogWarning(
                    "Authorization denied: required header {Header} is missing.",
                    AllowlistedCallerRequirement.HeaderName);
                context.Fail(new AuthorizationFailureReason(
                    this, $"Required header '{AllowlistedCallerRequirement.HeaderName}' is missing."));
                return Task.CompletedTask;
            }

            // Allowlist disabled: missing header is fine.
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var presented = headerValues[0]!.Trim();
        httpContext.Items[AllowlistedCallerRequirement.HeaderName] = presented;

        if (!allowlistEnabled)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (allowlist!.Any(entry => string.Equals(entry, presented, StringComparison.OrdinalIgnoreCase)))
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        _logger.LogWarning(
            "Authorization denied: caller repository {CallerRepository} is not in the allowlist.",
            presented);
        context.Fail(new AuthorizationFailureReason(
            this, $"Caller repository '{presented}' is not allowed."));
        return Task.CompletedTask;
    }
}
