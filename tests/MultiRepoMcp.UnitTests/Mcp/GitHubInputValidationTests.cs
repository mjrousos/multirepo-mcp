using FluentAssertions;
using MultiRepoMcp.Mcp;

namespace MultiRepoMcp.UnitTests.Mcp;

public class GitHubInputValidationTests
{
    [Theory]
    [InlineData("octocat", "Hello-World")]
    [InlineData("a", "b")]
    [InlineData("name-with-dashes", "name.with.dots")]
    [InlineData("Alpha9", "Repo_Name-1.2")]
    public void Accepts_valid_owner_and_repo(string owner, string repo)
    {
        var act = () => GitHubInputValidation.ValidateOwnerRepo(owner, repo);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("-leading-dash", "valid")]
    [InlineData("", "valid")]
    [InlineData(" ", "valid")]
    [InlineData("owner with spaces", "valid")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "valid")] // 40 chars > 39 max
    public void Rejects_invalid_owner(string owner, string repo)
    {
        var act = () => GitHubInputValidation.ValidateOwnerRepo(owner, repo);
        act.Should().Throw<ArgumentException>().And.ParamName.Should().Be("owner");
    }

    [Theory]
    [InlineData("valid", "")]
    [InlineData("valid", " ")]
    [InlineData("valid", "has spaces")]
    [InlineData("valid", "has/slash")]
    public void Rejects_invalid_repo(string owner, string repo)
    {
        var act = () => GitHubInputValidation.ValidateOwnerRepo(owner, repo);
        act.Should().Throw<ArgumentException>().And.ParamName.Should().Be("repo");
    }

    [Theory]
    [InlineData("/abs/path")]
    [InlineData("back\\slash")]
    [InlineData("contains\u0001ctl")]
    [InlineData("with\0null")]
    [InlineData("")]
    public void Rejects_invalid_path(string path)
    {
        var act = () => GitHubInputValidation.ValidatePath(path);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rejects_overlong_path()
    {
        var path = new string('a', 1025);
        var act = () => GitHubInputValidation.ValidatePath(path);
        act.Should().Throw<ArgumentException>().WithMessage("*1024*");
    }

    [Theory]
    [InlineData("src/Program.cs")]
    [InlineData("a")]
    [InlineData("deeply/nested/dir/file.txt")]
    public void Accepts_valid_path(string path)
    {
        var act = () => GitHubInputValidation.ValidatePath(path);
        act.Should().NotThrow();
    }

    [Fact]
    public void Null_ref_is_accepted_as_optional()
    {
        var act = () => GitHubInputValidation.ValidateRef(null);
        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("with\ttab")]
    [InlineData("with\u0001ctl")]
    public void Rejects_invalid_ref(string gitRef)
    {
        var act = () => GitHubInputValidation.ValidateRef(gitRef);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("main")]
    [InlineData("v1.2.3")]
    [InlineData("a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0")]
    [InlineData("feature/new-stuff")]
    public void Accepts_valid_ref(string gitRef)
    {
        var act = () => GitHubInputValidation.ValidateRef(gitRef);
        act.Should().NotThrow();
    }
}
