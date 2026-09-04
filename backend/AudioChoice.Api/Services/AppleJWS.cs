using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace AudioChoice.Api.Services;

/// <summary>
/// Verifies the signed JWS payloads Apple's App Store Server API and Server Notifications V2 use:
/// StoreKit2 transactions, renewal info, and notification envelopes.
/// </summary>
/// <remarks>
/// Distinct from <see cref="AppleIdentityToken"/>, which verifies Sign in with Apple identity
/// tokens against Apple's rotating JWKS. These JWS payloads are signed differently -- ES256, with
/// the signing certificate carried in the token's own header (<c>x5c</c>) rather than looked up by
/// key id -- so trust instead comes from checking that certificate chains up to Apple's root, which
/// is fixed and can be pinned in this server rather than fetched.
/// </remarks>
public static class AppleJWS
{
    /// <summary>
    /// Apple Root CA - G3, DER-encoded, base64. Downloaded from
    /// https://www.apple.com/certificateauthority/AppleRootCA-G3.cer and pinned here because it is
    /// the trust anchor Apple documents for the App Store Server Library across every language; it
    /// does not rotate the way Apple's Sign in With Apple JWKS does, and pinning it means this
    /// verification does not depend on a live fetch succeeding.
    /// </summary>
    private const string AppleRootCAG3Base64 =
        "MIICQzCCAcmgAwIBAgIILcX8iNLFS5UwCgYIKoZIzj0EAwMwZzEbMBkGA1UEAwwSQXBwbGUgUm9vdCBDQSAtIEczMSYwJAYDVQQLDB1BcHBsZSBDZXJ0aWZpY2F0aW9uIEF1dGhvcml0eTETMBEGA1UECgwKQXBwbGUgSW5jLjELMAkGA1UEBhMCVVMwHhcNMTQwNDMwMTgxOTA2WhcNMzkwNDMwMTgxOTA2WjBnMRswGQYDVQQDDBJBcHBsZSBSb290IENBIC0gRzMxJjAkBgNVBAsMHUFwcGxlIENlcnRpZmljYXRpb24gQXV0aG9yaXR5MRMwEQYDVQQKDApBcHBsZSBJbmMuMQswCQYDVQQGEwJVUzB2MBAGByqGSM49AgEGBSuBBAAiA2IABJjpLz1AcqTtkyJygRMc3RCV8cWjTnHcFBbZDuWmBSp3ZHtfTjjTuxxEtX/1H7YyYl3J6YRbTzBPEVoA/VhYDKX1DyxNB0cTddqXl5dvMVztK517IDvYuVTZXpmkOlEKMaNCMEAwHQYDVR0OBBYEFLuw3qFYM4iapIqZ3r6966/ayySrMA8GA1UdEwEB/wQFMAMBAf8wDgYDVR0PAQH/BAQDAgEGMAoGCCqGSM49BAMDA2gAMGUCMQCD6cHEFl4aXTQY2e3v9GwOAEZLuN+yRhHFD/3meoyhpmvOwgPUnPWTxnS4at+qIxUCMG1mihDK1A3UT82NQz60imOlM27jbdoXt2QfyFMm+YhidDkLF1vLUagM6BgD56KyKA==";

    /// <summary>
    /// Verifies the signature chain and returns the decoded payload, or null on any failure.
    /// </summary>
    /// <remarks>
    /// Every failure path -- a malformed token, an untrusted or expired certificate, a signature
    /// that does not verify -- returns null rather than throwing, so a caller cannot forget to
    /// handle one and treat unverified content as trusted by accident.
    /// </remarks>
    public static JsonDocument? VerifyAndDecode(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var parts = token.Split('.');
        if (parts.Length != 3) return null;

        X509Certificate2Collection certificates;
        string? algorithm;
        try
        {
            using var header = JsonDocument.Parse(AppleIdentityToken.Base64UrlDecode(parts[0]));
            if (!header.RootElement.TryGetProperty("alg", out var algElement)) return null;
            algorithm = algElement.GetString();
            if (!header.RootElement.TryGetProperty("x5c", out var chainElement) ||
                chainElement.ValueKind != JsonValueKind.Array) return null;

            certificates = new X509Certificate2Collection();
            foreach (var entry in chainElement.EnumerateArray())
            {
                var der = entry.GetString();
                if (der is null) return null;
#pragma warning disable SYSLIB0057 // X509CertificateLoader requires .NET 9; this codebase targets net8.0.
                certificates.Add(new X509Certificate2(Convert.FromBase64String(der)));
#pragma warning restore SYSLIB0057
            }
        }
        catch (Exception error) when (error is JsonException or FormatException or CryptographicException)
        {
            return null;
        }

        // Apple signs these with ES256 exclusively. Pinning it the same way AppleIdentityToken pins
        // RS256 blocks an attacker from naming a weaker or unexpected algorithm in the header.
        if (!string.Equals(algorithm, "ES256", StringComparison.Ordinal) || certificates.Count == 0) return null;

        var leaf = certificates[0];
        if (!ChainIsTrusted(certificates)) return null;

        try
        {
            using var publicKey = leaf.GetECDsaPublicKey();
            if (publicKey is null) return null;
            var signed = System.Text.Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
            // JWS ES256 signatures are the raw 64-byte R||S concatenation, not the DER encoding
            // ECDsa.VerifyData produces by default -- IeeeP1363 selects that wire format.
            if (!publicKey.VerifyData(
                    signed,
                    AppleIdentityToken.Base64UrlDecode(parts[2]),
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                return null;
            }
            return JsonDocument.Parse(AppleIdentityToken.Base64UrlDecode(parts[1]));
        }
        catch (Exception error) when (error is FormatException or CryptographicException or JsonException)
        {
            return null;
        }
        finally
        {
            foreach (var certificate in certificates) certificate.Dispose();
        }
    }

    private static bool ChainIsTrusted(X509Certificate2Collection certificates)
    {
#pragma warning disable SYSLIB0057 // X509CertificateLoader requires .NET 9; this codebase targets net8.0.
        using var root = new X509Certificate2(Convert.FromBase64String(AppleRootCAG3Base64));
#pragma warning restore SYSLIB0057
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        // Every certificate after the leaf is an intermediate the leaf's issuer supplies in the
        // same x5c array; without adding them here the chain cannot be built past the leaf.
        for (var index = 1; index < certificates.Count; index++)
        {
            chain.ChainPolicy.ExtraStore.Add(certificates[index]);
        }
        return chain.Build(certificates[0]);
    }
}
