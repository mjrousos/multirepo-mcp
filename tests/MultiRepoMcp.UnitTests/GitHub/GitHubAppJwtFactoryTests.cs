using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using MultiRepoMcp.Configuration;
using MultiRepoMcp.GitHub;

namespace MultiRepoMcp.UnitTests.GitHub;

public class GitHubAppJwtFactoryTests
{
    [Fact]
    public async Task Produces_jwt_with_correct_header_and_claims()
    {
        using var rsa = RSA.Create(2048);
        var keyProvider = new Mock<IGitHubPrivateKeyProvider>();
        keyProvider.Setup(k => k.GetPrivateKeyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(rsa);

        var now = DateTimeOffset.Parse("2030-06-15T12:00:00Z");
        var time = new FakeTimeProvider(now);

        var options = Options.Create(new GitHubAppOptions
        {
            AppId = 999_888,
            KeyVaultUri = new Uri("https://example.vault.azure.net/"),
            PrivateKeySecretName = "key",
            InstallationTokenRefreshThreshold = TimeSpan.FromMinutes(5),
        });

        var factory = new GitHubAppJwtFactory(keyProvider.Object, options, time);

        var jwt = await factory.CreateAsync(CancellationToken.None);

        var parts = jwt.Split('.');
        parts.Should().HaveCount(3);

        var header = DecodeJson(parts[0]);
        header.GetProperty("alg").GetString().Should().Be("RS256");
        header.GetProperty("typ").GetString().Should().Be("JWT");

        var payload = DecodeJson(parts[1]);
        payload.GetProperty("iss").GetInt64().Should().Be(999_888);
        payload.GetProperty("iat").GetInt64().Should().Be(now.AddSeconds(-60).ToUnixTimeSeconds());
        payload.GetProperty("exp").GetInt64().Should().Be(now.AddMinutes(9).ToUnixTimeSeconds());

        // sub is intentionally omitted (strict validators reject extras).
        payload.TryGetProperty("sub", out _).Should().BeFalse();

        // Signature verifies under RSA-SHA256 / PKCS#1 v1.5.
        var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        var signature = Base64UrlDecode(parts[2]);
        rsa.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .Should().BeTrue();
    }

    [Fact]
    public async Task Issues_fresh_jwts_on_each_call()
    {
        using var rsa = RSA.Create(2048);
        var keyProvider = new Mock<IGitHubPrivateKeyProvider>();
        keyProvider.Setup(k => k.GetPrivateKeyAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(rsa);

        var time = new FakeTimeProvider(DateTimeOffset.Parse("2030-06-15T12:00:00Z"));

        var factory = new GitHubAppJwtFactory(
            keyProvider.Object,
            Options.Create(new GitHubAppOptions
            {
                AppId = 1,
                KeyVaultUri = new Uri("https://example.vault.azure.net/"),
                PrivateKeySecretName = "k",
                InstallationTokenRefreshThreshold = TimeSpan.FromMinutes(5),
            }),
            time);

        var first = await factory.CreateAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(120));
        var second = await factory.CreateAsync(CancellationToken.None);

        first.Should().NotBe(second, "the iat/exp claims should change with the clock");
    }

    private static JsonElement DecodeJson(string base64Url)
    {
        var bytes = Base64UrlDecode(base64Url);
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }

    private static byte[] Base64UrlDecode(string s)
    {
        string padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}
