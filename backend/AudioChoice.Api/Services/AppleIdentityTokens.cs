using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioChoice.Api.Services;

/// <summary>
/// Supplies Apple's current public signing keys, indexed by key id.
/// </summary>
public interface IAppleSigningKeyProvider
{
    Task<IReadOnlyDictionary<string, RSA>> Keys(CancellationToken cancellationToken);
}

/// <summary>
/// Fetches and caches Apple's published JWKS.
/// </summary>
/// <remarks>
/// Apple rotates these keys, so they cannot be pinned, and a token signed with a key
/// we have not fetched yet must trigger a refresh rather than a rejection.
/// </remarks>
public sealed class AppleSigningKeyProvider(HttpClient client) : IAppleSigningKeyProvider
{
    private const string KeysUrl = "https://appleid.apple.com/auth/keys";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(6);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyDictionary<string, RSA> _cached = new Dictionary<string, RSA>();
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;

    public async Task<IReadOnlyDictionary<string, RSA>> Keys(CancellationToken cancellationToken)
    {
        if (_cached.Count > 0 && DateTimeOffset.UtcNow - _fetchedAt < CacheLifetime) return _cached;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached.Count > 0 && DateTimeOffset.UtcNow - _fetchedAt < CacheLifetime) return _cached;

            var document = await client.GetStringAsync(KeysUrl, cancellationToken);
            var parsed = AppleIdentityToken.ParseJsonWebKeySet(document);
            if (parsed.Count > 0)
            {
                _cached = parsed;
                _fetchedAt = DateTimeOffset.UtcNow;
            }
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// Cryptographic verification of Apple identity tokens.
/// </summary>
/// <remarks>
/// This exists because the audience, issuer and expiry claims are attacker-controlled
/// until the signature is checked. Reading them out of an unverified token and
/// treating the result as an identity lets anyone sign in as any Apple subject by
/// hand-assembling a JWT, so the signature check has to happen first and has to fail
/// closed.
/// </remarks>
public static class AppleIdentityToken
{
    public static IReadOnlyDictionary<string, RSA> ParseJsonWebKeySet(string document)
    {
        var keys = new Dictionary<string, RSA>(StringComparer.Ordinal);
        JsonWebKeySet? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<JsonWebKeySet>(document);
        }
        catch (JsonException)
        {
            return keys;
        }

        foreach (var key in parsed?.Keys ?? [])
        {
            if (!string.Equals(key.KeyType, "RSA", StringComparison.Ordinal)) continue;
            if (string.IsNullOrWhiteSpace(key.KeyID) ||
                string.IsNullOrWhiteSpace(key.Modulus) ||
                string.IsNullOrWhiteSpace(key.Exponent))
            {
                continue;
            }
            try
            {
                var rsa = RSA.Create();
                rsa.ImportParameters(new RSAParameters
                {
                    Modulus = Base64UrlDecode(key.Modulus),
                    Exponent = Base64UrlDecode(key.Exponent),
                });
                keys[key.KeyID] = rsa;
            }
            catch (Exception error) when (error is FormatException or CryptographicException)
            {
                // A single malformed entry must not discard the usable keys.
            }
        }
        return keys;
    }

    /// <summary>
    /// @return true only when the token is RS256, names a known key, and its
    ///   signature verifies. Every failure path returns false.
    /// </summary>
    public static bool SignatureIsValid(string? token, IReadOnlyDictionary<string, RSA> keys)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        var parts = token.Split('.');
        if (parts.Length != 3) return false;

        try
        {
            var header = JsonSerializer.Deserialize<JwtHeader>(Base64UrlDecode(parts[0]));
            // Pinning the algorithm blocks the "alg": "none" family of forgeries and
            // stops an HMAC token being validated against a public key.
            if (header is null || !string.Equals(header.Algorithm, "RS256", StringComparison.Ordinal)) return false;
            if (string.IsNullOrWhiteSpace(header.KeyID) || !keys.TryGetValue(header.KeyID, out var rsa)) return false;

            var signed = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
            return rsa.VerifyData(
                signed,
                Base64UrlDecode(parts[2]),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }
        catch (Exception error) when (error is JsonException or FormatException or CryptographicException)
        {
            return false;
        }
    }

    public static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }

    private sealed record JwtHeader(
        [property: JsonPropertyName("alg")] string? Algorithm,
        [property: JsonPropertyName("kid")] string? KeyID);

    private sealed record JsonWebKeySet(
        [property: JsonPropertyName("keys")] IReadOnlyList<JsonWebKey>? Keys);

    private sealed record JsonWebKey(
        [property: JsonPropertyName("kty")] string? KeyType,
        [property: JsonPropertyName("kid")] string? KeyID,
        [property: JsonPropertyName("n")] string? Modulus,
        [property: JsonPropertyName("e")] string? Exponent);
}
