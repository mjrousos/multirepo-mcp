using Azure;
using Azure.Core;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MultiRepoMcp.Configuration;
using MultiRepoMcp.GitHub;

namespace MultiRepoMcp.UnitTests.GitHub;

public class KeyVaultJwtSignerTests
{
    [Fact]
    public async Task Returns_signature_from_underlying_signing_operation()
    {
        var expected = new byte[] { 1, 2, 3, 4, 5 };
        byte[]? observedDigest = null;
        var signer = NewSigner((digest, _) =>
        {
            observedDigest = digest;
            return ValueTask.FromResult(expected);
        });

        var input = new byte[32];
        for (var i = 0; i < input.Length; i++) input[i] = (byte)i;

        var sig = await signer.SignDigestAsync(input, CancellationToken.None);

        sig.Should().Equal(expected);
        observedDigest.Should().Equal(input,
            "the signer must hand the unmodified SHA-256 digest to the Key Vault sign operation");
    }

    [Fact]
    public async Task Wraps_RequestFailedException_in_InvalidOperationException()
    {
        var signer = NewSigner((_, _) =>
            throw new RequestFailedException(403, "Forbidden"));

        var act = async () => await signer.SignDigestAsync(new byte[32], CancellationToken.None);
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Failed to sign GitHub App JWT via Key Vault*")
            .WithInnerException<RequestFailedException>();
    }

    [Fact]
    public async Task Propagates_OperationCanceledException_when_caller_cancels()
    {
        using var cts = new CancellationTokenSource();
        var signer = NewSigner((_, ct) =>
            throw new OperationCanceledException(ct));

        cts.Cancel();
        var act = async () => await signer.SignDigestAsync(new byte[32], cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Constructor_throws_when_KeyVaultUri_missing_and_no_test_seam()
    {
        var act = () => new KeyVaultJwtSigner(
            Options.Create(new GitHubAppOptions
            {
                AppId = 1,
                PrivateKeyName = "k",
                // KeyVaultUri intentionally null
            }),
            NullLogger<KeyVaultJwtSigner>.Instance,
            credential: new FakeCredential(),
            signOperationForTests: null);
        act.Should().Throw<InvalidOperationException>().WithMessage("*KeyVaultUri*");
    }

    [Fact]
    public void Constructor_throws_when_PrivateKeyName_missing_and_no_test_seam()
    {
        var act = () => new KeyVaultJwtSigner(
            Options.Create(new GitHubAppOptions
            {
                AppId = 1,
                KeyVaultUri = new Uri("https://example.vault.azure.net/"),
                PrivateKeyName = string.Empty,
            }),
            NullLogger<KeyVaultJwtSigner>.Instance,
            credential: new FakeCredential(),
            signOperationForTests: null);
        act.Should().Throw<InvalidOperationException>().WithMessage("*PrivateKeyName*");
    }

    private static KeyVaultJwtSigner NewSigner(
        Func<byte[], CancellationToken, ValueTask<byte[]>> signOperation) => new(
        Options.Create(new GitHubAppOptions
        {
            AppId = 1,
            KeyVaultUri = new Uri("https://example.vault.azure.net/"),
            PrivateKeyName = "k",
        }),
        NullLogger<KeyVaultJwtSigner>.Instance,
        credential: new FakeCredential(),
        signOperationForTests: signOperation);

    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new("fake", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(GetToken(requestContext, cancellationToken));
    }
}
