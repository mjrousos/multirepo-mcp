using FluentAssertions;
using MultiRepoMcp.Mcp;

namespace MultiRepoMcp.UnitTests.Mcp;

public class SearchCodeQueryValidationTests
{
    [Theory]
    [InlineData("repo:foo")]
    [InlineData("REPO:foo")]
    [InlineData("Repo:foo")]
    [InlineData("user:bar")]
    [InlineData("org:baz")]
    [InlineData("owner:baz")]
    [InlineData("path:src/")]
    [InlineData("language:csharp")]
    [InlineData("extension:cs")]
    [InlineData("filename:Program.cs")]
    [InlineData("is:open")]
    [InlineData("in:body")]
    [InlineData("size:>100")]
    [InlineData("hello repo:foo world")]
    [InlineData("foo bar  language:cs")]
    [InlineData("some-newqualifier:value")]
    public void Rejects_any_qualifier(string query)
    {
        var act = () => SearchCodeQueryValidation.Validate(query);
        act.Should().Throw<ArgumentException>().WithMessage("*qualifier*");
    }

    [Theory]
    [InlineData("foo OR bar")]
    [InlineData("foo NOT bar")]
    [InlineData("OR")]
    [InlineData("hello OR baz qux")]
    public void Rejects_boolean_operators(string query)
    {
        var act = () => SearchCodeQueryValidation.Validate(query);
        act.Should().Throw<ArgumentException>().WithMessage("*boolean*");
    }

    [Theory]
    [InlineData("-foo")]
    [InlineData("hello -foo world")]
    [InlineData("baz -bar")]
    public void Rejects_leading_dash_exclusions(string query)
    {
        var act = () => SearchCodeQueryValidation.Validate(query);
        act.Should().Throw<ArgumentException>().WithMessage("*leading-dash*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_empty_query(string query)
    {
        var act = () => SearchCodeQueryValidation.Validate(query);
        act.Should().Throw<ArgumentException>().WithMessage("*required*");
    }

    [Fact]
    public void Rejects_overlong_query()
    {
        var query = new string('a', 1025);
        var act = () => SearchCodeQueryValidation.Validate(query);
        act.Should().Throw<ArgumentException>().WithMessage("*1024 characters*");
    }

    [Theory]
    [InlineData("\"repo:foo\"")]
    [InlineData("hello \"repo:foo\" world")]
    [InlineData("\"NOT a problem\"")]
    [InlineData("\"-leading dash inside\"")]
    [InlineData("plain words only")]
    [InlineData("UseSetTheRunOption")]
    [InlineData("function ParseInt")]
    public void Accepts_legitimate_queries(string query)
    {
        var act = () => SearchCodeQueryValidation.Validate(query);
        act.Should().NotThrow();
    }
}
