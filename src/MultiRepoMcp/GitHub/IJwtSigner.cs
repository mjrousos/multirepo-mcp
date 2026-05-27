namespace MultiRepoMcp.GitHub;

/// <summary>
/// Performs the RS256 signing step of a GitHub App JWT.
///
/// The contract intentionally takes a pre-computed SHA-256 digest of the
/// JWT signing input (<c>base64url(header) + "." + base64url(payload)</c>),
/// not the raw bytes — this lets the implementation hand the digest off to
/// Azure Key Vault's "sign digest" API without ever exposing the private key
/// material to this process.
/// </summary>
internal interface IJwtSigner
{
    /// <summary>
    /// Signs <paramref name="digest"/> (a 32-byte SHA-256 hash) and returns
    /// the raw RSA signature bytes (big-endian, what RSA-PKCS#1 v1.5 emits
    /// and what GitHub expects when base64url-encoded as the JWT signature).
    /// </summary>
    ValueTask<byte[]> SignDigestAsync(byte[] digest, CancellationToken cancellationToken);
}
