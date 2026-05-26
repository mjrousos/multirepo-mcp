using FluentAssertions;
using MultiRepoMcp.Configuration;

namespace MultiRepoMcp.UnitTests.Configuration;

public class AuthenticationOptionsValidatorTests
{
    private readonly AuthenticationOptionsValidator _sut = new();

    [Theory]
    [InlineData("changeme")]
    [InlineData("CHANGEME")]
    [InlineData("ChangeMe")]
    [InlineData("...")]
    [InlineData("todo")]
    [InlineData("replace-me")]
    [InlineData("your-token-here")]
    [InlineData("your-bearer-token")]
    [InlineData("secret")]
    [InlineData("token")]
    public void Rejects_known_placeholder_bearer_tokens(string placeholder)
    {
        var options = new AuthenticationOptions { BearerToken = placeholder };

        var result = _sut.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("placeholder");
    }

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaa")]
    [InlineData("xxxxxxxxxxxxxxxxxxxxxxxx")]
    [InlineData("11111111111111111111")]
    public void Rejects_repeated_character_bearer_tokens(string repeated)
    {
        var options = new AuthenticationOptions { BearerToken = repeated };

        var result = _sut.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("repeated characters");
    }

    [Fact]
    public void Accepts_a_well_formed_bearer_token()
    {
        var options = new AuthenticationOptions { BearerToken = "AbCdEf01234567890_XYZ-token_value" };

        var result = _sut.Validate(name: null, options);

        result.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData("not-a-valid-pair")]
    [InlineData("/leading-slash")]
    [InlineData("only/")]
    [InlineData("/only")]
    [InlineData("a/b/c")]
    public void Rejects_malformed_allowlist_entries(string entry)
    {
        var options = new AuthenticationOptions
        {
            BearerToken = "AbCdEf01234567890_XYZ-token_value",
            CallerRepositoryAllowlist = new[] { entry },
        };

        var result = _sut.Validate(name: null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain(entry);
    }

    [Fact]
    public void Accepts_well_formed_allowlist()
    {
        var options = new AuthenticationOptions
        {
            BearerToken = "AbCdEf01234567890_XYZ-token_value",
            CallerRepositoryAllowlist = new[] { "octo/hello", "octo/world" },
        };

        var result = _sut.Validate(name: null, options);

        result.Succeeded.Should().BeTrue();
    }
}
