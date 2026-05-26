using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace MultiRepoMcp.Authentication;

internal static class AuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddMultiRepoMcpAuthentication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpContextAccessor();

        services.AddAuthentication(StaticBearerAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, StaticBearerAuthenticationHandler>(
                StaticBearerAuthenticationHandler.SchemeName,
                _ => { });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(
                AllowlistedCallerRequirement.PolicyName,
                policy =>
                {
                    policy.AuthenticationSchemes.Add(StaticBearerAuthenticationHandler.SchemeName);
                    policy.RequireAuthenticatedUser();
                    policy.AddRequirements(new AllowlistedCallerRequirement());
                });
        });

        services.AddSingleton<IAuthorizationHandler, AllowlistedCallerAuthorizationHandler>();

        return services;
    }
}
