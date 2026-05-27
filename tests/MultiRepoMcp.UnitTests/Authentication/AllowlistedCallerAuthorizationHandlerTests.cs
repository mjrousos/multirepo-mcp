using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MultiRepoMcp.Authentication;
using AppAuthenticationOptions = MultiRepoMcp.Configuration.AuthenticationOptions;

namespace MultiRepoMcp.UnitTests.Authentication;

public class AllowlistedCallerAuthorizationHandlerTests
{
    [Fact]
    public async Task Succeeds_when_allowlist_disabled_and_header_missing()
    {
        var result = await EvaluateAsync(allowlist: null, callerHeader: null);
        result.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task Succeeds_when_allowlist_disabled_and_header_present()
    {
        var result = await EvaluateAsync(allowlist: null, callerHeader: "anything/here");
        result.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task Fails_when_allowlist_enabled_and_header_missing()
    {
        var result = await EvaluateAsync(
            allowlist: new[] { "octo/cat" },
            callerHeader: null);

        result.HasSucceeded.Should().BeFalse();
        result.HasFailed.Should().BeTrue();
    }

    [Fact]
    public async Task Fails_when_allowlist_enabled_and_caller_not_listed()
    {
        var result = await EvaluateAsync(
            allowlist: new[] { "octo/cat" },
            callerHeader: "stranger/danger");

        result.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Succeeds_when_caller_is_allowlisted()
    {
        var result = await EvaluateAsync(
            allowlist: new[] { "octo/cat" },
            callerHeader: "octo/cat");

        result.HasSucceeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("octo/cat", "Octo/Cat")]
    [InlineData("octo/cat", "OCTO/CAT")]
    [InlineData("Mixed/Case", "mixed/case")]
    public async Task Allowlist_match_is_case_insensitive(string configured, string presented)
    {
        var result = await EvaluateAsync(
            allowlist: new[] { configured },
            callerHeader: presented);

        result.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task Fails_when_header_is_only_whitespace()
    {
        var result = await EvaluateAsync(
            allowlist: new[] { "octo/cat" },
            callerHeader: "   ");

        result.HasSucceeded.Should().BeFalse();
    }

    private static async Task<AuthorizationHandlerContext> EvaluateAsync(
        IReadOnlyList<string>? allowlist,
        string? callerHeader)
    {
        var httpContext = new DefaultHttpContext();
        if (callerHeader is not null)
        {
            httpContext.Request.Headers[AllowlistedCallerRequirement.HeaderName] = callerHeader;
        }

        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var optionsMonitor = new TestOptionsMonitor<AppAuthenticationOptions>(
            new AppAuthenticationOptions
            {
                BearerToken = "doesnt-matter-for-this-test",
                CallerRepositoryAllowlist = allowlist,
            });

        var handler = new AllowlistedCallerAuthorizationHandler(
            optionsMonitor,
            accessor,
            NullLogger<AllowlistedCallerAuthorizationHandler>.Instance);

        var requirement = new AllowlistedCallerRequirement();
        var user = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "TestAuth"));
        var context = new AuthorizationHandlerContext(
            new IAuthorizationRequirement[] { requirement },
            user,
            resource: null);

        await handler.HandleAsync(context);
        return context;
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
