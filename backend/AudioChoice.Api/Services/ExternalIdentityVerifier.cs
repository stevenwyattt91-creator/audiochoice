using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
#if GOOGLEAUTH
using Google.Apis.Auth;
#endif

namespace AudioChoice.Api.Services;

public sealed class ExternalAuthOptions
{
    public string AppleClientID { get; init; } = string.Empty;
    public string AppleClientSecret { get; init; } = string.Empty;
    public string GoogleClientID { get; init; } = string.Empty;

    public IReadOnlyList<string> GoogleClientIDs => GoogleClientID
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    public IReadOnlyList<string> AppleClientIDs => AppleClientID
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}

public sealed record VerifiedIdentity(
    string Provider,
    string Subject,
    string Email,
    bool EmailVerified);

public sealed class ExternalIdentityVerifier(
    HttpClient client,
    ExternalAuthOptions options,
    ILogger<ExternalIdentityVerifier> logger)
{
    public async Task<VerifiedIdentity?> Verify(
        string provider,
        string authorizationCode,
        string? identityToken,
        CancellationToken cancellationToken)
    {
        return provider.ToLowerInvariant() switch
        {
            "apple" => await VerifyApple(authorizationCode, identityToken, cancellationToken),
            "google" => await VerifyGoogle(identityToken, cancellationToken),
            _ => null
        };
    }

    private async Task<VerifiedIdentity?> VerifyApple(string code, string? identityToken, CancellationToken cancellationToken)
    {
        // Native iOS sign-in returns an identity token whose audience is the
        // primary App ID. Web Sign in with Apple uses the grouped Services ID.
        // Accept either audience so both paths resolve to the same Apple subject.
        var direct = ParseAppleIdentity(identityToken);
        if (direct is not null) return direct;
        if (options.AppleClientIDs.Count == 0 ||
            string.IsNullOrWhiteSpace(options.AppleClientSecret) ||
            string.IsNullOrWhiteSpace(code)) return null;
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = options.AppleClientIDs[0],
            ["client_secret"] = options.AppleClientSecret,
            ["code"] = code,
            ["grant_type"] = "authorization_code"
        });
        using var response = await client.PostAsync("https://appleid.apple.com/auth/token", content, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        var token = await response.Content.ReadFromJsonAsync<AppleTokenResponse>(cancellationToken: cancellationToken);
        return ParseAppleIdentity(token?.IdentityToken);
    }

    private VerifiedIdentity? ParseAppleIdentity(string? token)
    {
        try
        {
            var payload = token?.Split('.')[1].Replace('-', '+').Replace('_', '/');
            if (payload is null) return null;
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var claims = JsonSerializer.Deserialize<AppleClaims>(Convert.FromBase64String(payload));
            if (claims is null || !options.AppleClientIDs.Contains(claims.Audience, StringComparer.Ordinal) || claims.Issuer != "https://appleid.apple.com" || claims.ExpiresAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return null;
            return new VerifiedIdentity("apple", claims.Subject, claims.Email ?? string.Empty, false);
        }
        catch { return null; }
    }

    private async Task<VerifiedIdentity?> VerifyGoogle(string? token, CancellationToken cancellationToken)
    {
        var acceptedAudiences = options.GoogleClientIDs;
        if (acceptedAudiences.Count == 0 || string.IsNullOrWhiteSpace(token)) return null;
#if GOOGLEAUTH
        try
        {
            var payload = await GoogleJsonWebSignature.ValidateAsync(
                token,
                new GoogleJsonWebSignature.ValidationSettings
                {
                    Audience = acceptedAudiences
                });
            if (string.IsNullOrWhiteSpace(payload.Subject) ||
                string.IsNullOrWhiteSpace(payload.Email) ||
                !payload.EmailVerified)
            {
                return null;
            }
            return new VerifiedIdentity(
                "google", payload.Subject, payload.Email, true);
        }
        catch (InvalidJwtException exception)
        {
            logger.LogWarning(
                "Google ID token validation failed for configured audiences {Audiences}: {Reason}",
                string.Join(",", acceptedAudiences),
                exception.Message);
            return null;
        }
#else
        var url = "https://oauth2.googleapis.com/tokeninfo?id_token=" + Uri.EscapeDataString(token);
        try
        {
            var claims = await client.GetFromJsonAsync<GoogleClaims>(url, cancellationToken);
            if (claims is null ||
                !acceptedAudiences.Contains(claims.Audience, StringComparer.Ordinal) ||
                string.IsNullOrWhiteSpace(claims.Subject) ||
                string.IsNullOrWhiteSpace(claims.Email) ||
                !string.Equals(claims.EmailVerified, "true", StringComparison.OrdinalIgnoreCase)) return null;
            return new VerifiedIdentity("google", claims.Subject, claims.Email, true);
        }
        catch (HttpRequestException) { return null; }
#endif
    }

    private sealed record AppleTokenResponse([property: JsonPropertyName("id_token")] string IdentityToken);
    private sealed record AppleClaims(
        [property: JsonPropertyName("iss")] string Issuer,
        [property: JsonPropertyName("aud")] string Audience,
        [property: JsonPropertyName("sub")] string Subject,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("exp")] long ExpiresAt);
    private sealed record GoogleClaims(
        [property: JsonPropertyName("aud")] string Audience,
        [property: JsonPropertyName("sub")] string Subject,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("email_verified")] string? EmailVerified);
}
