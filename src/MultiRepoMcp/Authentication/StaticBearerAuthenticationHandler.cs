using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AppAuthenticationOptions = MultiRepoMcp.Configuration.AuthenticationOptions;

namespace MultiRepoMcp.Authentication;

/// <summary>
/// Validates an <c>Authorization: Bearer &lt;token&gt;</c> header against the
/// configured static bearer token using constant-time comparison.
/// </summary>
internal sealed class StaticBearerAuthenticationHandler
    : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "StaticBearer";
    private const string BearerPrefix = "Bearer ";

    private readonly IOptionsMonitor<AppAuthenticationOptions> _authOptions;

    public StaticBearerAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptionsMonitor<AppAuthenticationOptions> authOptions)
        : base(options, logger, encoder)
    {
        _authOptions = authOptions;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var values) || values.Count == 0)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string? headerValue = values[0];
        if (string.IsNullOrEmpty(headerValue) || !headerValue.StartsWith(BearerPrefix, StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var presentedToken = headerValue.AsSpan(BearerPrefix.Length).Trim().ToString();
        if (presentedToken.Length == 0)
        {
            return Task.FromResult(AuthenticateResult.Fail("Empty bearer token."));
        }

        var configured = _authOptions.CurrentValue.BearerToken;
        if (string.IsNullOrEmpty(configured))
        {
            // Misconfiguration: startup validation should have caught this.
            Logger.LogError("StaticBearer scheme is enabled but no bearer token is configured.");
            return Task.FromResult(AuthenticateResult.Fail("Server bearer token is not configured."));
        }

        if (!ConstantTimeEquals(presentedToken, configured))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid bearer token."));
        }

        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "static-bearer") },
            SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers["WWW-Authenticate"] = "Bearer";
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    private static bool ConstantTimeEquals(string a, string b)
    {
        // FixedTimeEquals requires equal-length spans; encode UTF-8 first so an
        // attacker cannot probe length via comparison short-circuit.
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        if (aBytes.Length != bBytes.Length)
        {
            // Still touch a same-length buffer so timing remains bounded.
            CryptographicOperations.FixedTimeEquals(aBytes, aBytes);
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
