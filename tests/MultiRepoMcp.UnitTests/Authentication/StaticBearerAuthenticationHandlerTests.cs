using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MultiRepoMcp.Authentication;
using AppAuthenticationOptions = MultiRepoMcp.Configuration.AuthenticationOptions;

namespace MultiRepoMcp.UnitTests.Authentication;

public class StaticBearerAuthenticationHandlerTests
{
    private const string ConfiguredToken = "the-quick-brown-fox-jumps-over-32";

    [Fact]
    public async Task Returns_NoResult_when_Authorization_header_missing()
    {
        var handler = await CreateHandlerAsync(ConfiguredToken, headerValue: null);

        var result = await handler.AuthenticateAsync();

        result.None.Should().BeTrue();
        result.Failure.Should().BeNull();
    }

    [Fact]
    public async Task Returns_NoResult_when_Authorization_header_lacks_Bearer_prefix()
    {
        var handler = await CreateHandlerAsync(ConfiguredToken, headerValue: "Basic dXNlcjpwYXNz");

        var result = await handler.AuthenticateAsync();

        result.None.Should().BeTrue();
    }

    [Fact]
    public async Task Fails_when_Bearer_token_is_empty()
    {
        var handler = await CreateHandlerAsync(ConfiguredToken, headerValue: "Bearer ");

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Contain("Empty");
    }

    [Fact]
    public async Task Fails_when_presented_token_does_not_match()
    {
        var handler = await CreateHandlerAsync(ConfiguredToken, headerValue: "Bearer wrong-but-long-enough-token-32");

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Contain("Invalid bearer token");
    }

    [Fact]
    public async Task Fails_when_presented_token_differs_in_length()
    {
        // FixedTimeEquals requires equal-length spans; verify the length branch returns false (not throws).
        var handler = await CreateHandlerAsync(ConfiguredToken, headerValue: "Bearer short");

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Contain("Invalid bearer token");
    }

    [Fact]
    public async Task Succeeds_when_presented_token_matches()
    {
        var handler = await CreateHandlerAsync(ConfiguredToken, headerValue: $"Bearer {ConfiguredToken}");

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeTrue();
        result.Principal!.Identity!.IsAuthenticated.Should().BeTrue();
        result.Principal.Claims.Should().Contain(c => c.Type == ClaimTypes.Name);
    }

    [Fact]
    public async Task Fails_when_no_configured_token()
    {
        var handler = await CreateHandlerAsync(configuredToken: "", headerValue: $"Bearer {ConfiguredToken}");

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeFalse();
        result.Failure!.Message.Should().Contain("not configured");
    }

    [Fact]
    public async Task Challenge_writes_401_with_Bearer_WWW_Authenticate()
    {
        var (handler, ctx) = await CreateHandlerWithContextAsync(ConfiguredToken, headerValue: null);

        await handler.ChallengeAsync(properties: null);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        ctx.Response.Headers.WWWAuthenticate.ToString().Should().Be("Bearer");
    }

    private static async Task<StaticBearerAuthenticationHandler> CreateHandlerAsync(string configuredToken, string? headerValue)
    {
        var (handler, _) = await CreateHandlerWithContextAsync(configuredToken, headerValue);
        return handler;
    }

    private static async Task<(StaticBearerAuthenticationHandler Handler, HttpContext Context)> CreateHandlerWithContextAsync(
        string configuredToken,
        string? headerValue)
    {
        var optionsMonitor = new TestOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions());
        var authOptions = new TestOptionsMonitor<AppAuthenticationOptions>(
            new AppAuthenticationOptions { BearerToken = configuredToken });

        var handler = new StaticBearerAuthenticationHandler(
            optionsMonitor,
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            authOptions);

        var context = new DefaultHttpContext();
        if (headerValue is not null)
        {
            context.Request.Headers.Authorization = headerValue;
        }

        var scheme = new AuthenticationScheme(
            StaticBearerAuthenticationHandler.SchemeName,
            displayName: null,
            handlerType: typeof(StaticBearerAuthenticationHandler));
        await handler.InitializeAsync(scheme, context);

        return (handler, context);
    }

    private sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
        where T : class
    {
        private readonly T _value;
        public TestOptionsMonitor(T value) => _value = value;
        public T CurrentValue => _value;
        public T Get(string? name) => _value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
