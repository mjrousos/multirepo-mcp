using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MultiRepoMcp.Configuration;
using MultiRepoMcp.GitHub;

namespace MultiRepoMcp.UnitTests.GitHub;

public sealed class LocalPemJwtSignerTests : IDisposable
{
    private readonly string _pemPath = Path.Combine(Path.GetTempPath(), $"mrm-test-{Guid.NewGuid():N}.pem");

    public void Dispose()
    {
        if (File.Exists(_pemPath))
        {
            File.Delete(_pemPath);
        }
    }

    [Fact]
    public async Task Signs_digest_with_PEM_key_so_signature_verifies()
    {
        using var rsa = RSA.Create(2048);
        File.WriteAllText(_pemPath, rsa.ExportRSAPrivateKeyPem());

        var signer = NewSigner();

        var payload = Encoding.ASCII.GetBytes("hello world");
        var digest = SHA256.HashData(payload);
        var signature = await signer.SignDigestAsync(digest, CancellationToken.None);

        rsa.VerifyHash(digest, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .Should().BeTrue();
    }

    [Fact]
    public async Task Caches_the_RSA_instance_across_calls()
    {
        using var rsa = RSA.Create(2048);
        File.WriteAllText(_pemPath, rsa.ExportRSAPrivateKeyPem());

        var signer = NewSigner();

        var digest = SHA256.HashData(Encoding.ASCII.GetBytes("payload"));
        var first = await signer.SignDigestAsync(digest, CancellationToken.None);

        // Delete the PEM and confirm the second sign still works (cached RSA).
        File.Delete(_pemPath);
        var second = await signer.SignDigestAsync(digest, CancellationToken.None);

        first.Should().Equal(second, "RSA-PKCS#1 v1.5 over a fixed digest is deterministic");
    }

    [Fact]
    public async Task Throws_when_PEM_is_invalid()
    {
        File.WriteAllText(_pemPath, "this is not a PEM file");

        var signer = NewSigner();

        var act = async () =>
            await signer.SignDigestAsync(SHA256.HashData(new byte[] { 1, 2, 3 }), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not a valid PEM*");
    }

    private LocalPemJwtSigner NewSigner() => new(
        Options.Create(new GitHubAppOptions
        {
            AppId = 1,
            KeyVaultUri = new Uri("https://example.vault.azure.net/"),
            PrivateKeyName = "k",
            LocalPrivateKeyPath = _pemPath,
        }),
        NullLogger<LocalPemJwtSigner>.Instance);
}
