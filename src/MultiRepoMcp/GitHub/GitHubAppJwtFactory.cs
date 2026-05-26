using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MultiRepoMcp.Configuration;

namespace MultiRepoMcp.GitHub;

/// <summary>
/// Creates GitHub App JWTs with RS256 signing per
/// https://docs.github.com/apps/creating-github-apps/authenticating-with-a-github-app/generating-a-json-web-token-jwt-for-a-github-app.
///
/// Claims:
///   iss = GitHub App ID
///   iat = now - 60 seconds (tolerate clock skew with GitHub)
///   exp = now + 9 minutes  (under GitHub's 10-minute cap, with safety margin)
///   sub is intentionally OMITTED (not required, strict validators reject extras).
/// </summary>
internal sealed class GitHubAppJwtFactory : IGitHubAppJwtFactory
{
    private static readonly byte[] HeaderBytes = JsonSerializer.SerializeToUtf8Bytes(
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["alg"] = "RS256",
            ["typ"] = "JWT",
        });

    private static readonly TimeSpan IssuedAtBackdate = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(9);

    private readonly IGitHubPrivateKeyProvider _privateKeyProvider;
    private readonly GitHubAppOptions _options;
    private readonly TimeProvider _timeProvider;

    public GitHubAppJwtFactory(
        IGitHubPrivateKeyProvider privateKeyProvider,
        IOptions<GitHubAppOptions> options,
        TimeProvider timeProvider)
    {
        _privateKeyProvider = privateKeyProvider;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public async ValueTask<string> CreateAsync(CancellationToken cancellationToken)
    {
        var key = await _privateKeyProvider.GetPrivateKeyAsync(cancellationToken).ConfigureAwait(false);

        var now = _timeProvider.GetUtcNow();
        var iat = now - IssuedAtBackdate;
        var exp = now + TokenLifetime;

        var payload = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["iat"] = iat.ToUnixTimeSeconds(),
            ["exp"] = exp.ToUnixTimeSeconds(),
            ["iss"] = _options.AppId,
        };
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);

        var headerEncoded = Base64UrlEncode(HeaderBytes);
        var payloadEncoded = Base64UrlEncode(payloadBytes);
        var signingInput = $"{headerEncoded}.{payloadEncoded}";
        var signingInputBytes = Encoding.ASCII.GetBytes(signingInput);

        byte[] signature = key.SignData(
            signingInputBytes,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(byte[] data)
    {
        // RFC 7515 base64url: standard base64 with +/= → -_ (and no padding).
        var base64 = Convert.ToBase64String(data);
        return base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
