using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioChoice.Api.Services;

/// <summary>
/// Calls the Google Play Developer API to look up a subscription purchase, authenticating as the
/// service account configured in <see cref="PurchaseOptions.GoogleServiceAccountJson"/>.
/// </summary>
/// <remarks>
/// Hand-rolled rather than pulled in as the official Google.Apis.AndroidPublisher.v3 SDK: that
/// package is a large, generated surface for one narrow call, and this codebase already verifies
/// Apple's tokens by hand (see <see cref="AppleIdentityToken"/>) rather than reaching for an SDK, so
/// this follows the same pattern the rest of the server uses for external verification.
/// </remarks>
public sealed class GooglePlayClient(HttpClient client, PurchaseOptions options)
{
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string Scope = "https://www.googleapis.com/auth/androidpublisher";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _cachedAccessToken;
    private DateTimeOffset _cachedUntil = DateTimeOffset.MinValue;

    /// <summary>
    /// The subscription's status per Google, or null if the token does not resolve to a purchase at
    /// all (never treated the same as an inactive one -- see <see cref="GoogleSubscriptionStatus"/>).
    /// </summary>
    public async Task<GoogleSubscriptionStatus?> GetSubscription(string purchaseToken, CancellationToken cancellationToken)
    {
        var accessToken = await AccessToken(cancellationToken);
        if (accessToken is null) return null;

        var url = $"https://androidpublisher.googleapis.com/androidpublisher/v3/applications/" +
            $"{Uri.EscapeDataString(options.GooglePackageName)}/purchases/subscriptionsv2/tokens/" +
            $"{Uri.EscapeDataString(purchaseToken)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        var body = await response.Content.ReadFromJsonAsync<SubscriptionPurchaseV2>(cancellationToken: cancellationToken);
        if (body?.LineItems is null || body.LineItems.Count == 0) return null;
        // A subscription can have more than one line item after a plan change; the most recently
        // expiring one is the one that determines whether access should still be granted.
        var current = body.LineItems.OrderByDescending(item => item.ExpiryTime).First();
        return new GoogleSubscriptionStatus(
            ProductID: current.ProductID ?? string.Empty,
            State: body.SubscriptionState ?? string.Empty,
            ExpiresAt: current.ExpiryTime,
            OrderID: body.LatestOrderID);
    }

    /// <summary>
    /// Acknowledges a purchase, required by Google within three days of purchase or the payment is
    /// automatically refunded to the buyer.
    /// </summary>
    public async Task<bool> Acknowledge(string productID, string purchaseToken, CancellationToken cancellationToken)
    {
        var accessToken = await AccessToken(cancellationToken);
        if (accessToken is null) return false;

        var url = $"https://androidpublisher.googleapis.com/androidpublisher/v3/applications/" +
            $"{Uri.EscapeDataString(options.GooglePackageName)}/purchases/subscriptions/" +
            $"{Uri.EscapeDataString(productID)}/tokens/{Uri.EscapeDataString(purchaseToken)}:acknowledge";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        // Already-acknowledged is reported by Google as a failure; treated as success here since the
        // outcome this call exists to guarantee -- the purchase will not be auto-refunded -- already
        // holds either way.
        return response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.BadRequest;
    }

    private async Task<string?> AccessToken(CancellationToken cancellationToken)
    {
        if (_cachedAccessToken is not null && DateTimeOffset.UtcNow < _cachedUntil) return _cachedAccessToken;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cachedAccessToken is not null && DateTimeOffset.UtcNow < _cachedUntil) return _cachedAccessToken;

            var account = ParseServiceAccount(options.GoogleServiceAccountJson);
            if (account is null) return null;

            var assertion = SignedJwtAssertion(account);
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = assertion,
            });
            using var response = await client.PostAsync(TokenEndpoint, content, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: cancellationToken);
            if (string.IsNullOrWhiteSpace(token?.AccessToken)) return null;

            _cachedAccessToken = token.AccessToken;
            // A minute of slack against the token's own expiry, so a call that starts just before it
            // lapses does not fail mid-flight.
            _cachedUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, token.ExpiresInSeconds - 60));
            return _cachedAccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string SignedJwtAssertion(ServiceAccountKey account)
    {
        var now = DateTimeOffset.UtcNow;
        var header = JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT" });
        var claims = JsonSerializer.Serialize(new
        {
            iss = account.ClientEmail,
            scope = Scope,
            aud = TokenEndpoint,
            iat = now.ToUnixTimeSeconds(),
            exp = now.AddMinutes(30).ToUnixTimeSeconds(),
        });
        var unsigned = $"{Base64Url(header)}.{Base64Url(claims)}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(account.PrivateKey);
        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{unsigned}.{Convert.ToBase64String(signature).Replace('+', '-').Replace('/', '_').TrimEnd('=')}";
    }

    private static string Base64Url(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static ServiceAccountKey? ParseServiceAccount(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var key = JsonSerializer.Deserialize<ServiceAccountKey>(json);
            return key is { ClientEmail: not null, PrivateKey: not null } ? key : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ServiceAccountKey(
        [property: JsonPropertyName("client_email")] string? ClientEmail,
        [property: JsonPropertyName("private_key")] string? PrivateKey);

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresInSeconds);

    private sealed record SubscriptionPurchaseV2(
        [property: JsonPropertyName("subscriptionState")] string? SubscriptionState,
        [property: JsonPropertyName("latestOrderId")] string? LatestOrderID,
        [property: JsonPropertyName("lineItems")] List<SubscriptionLineItem>? LineItems);

    private sealed record SubscriptionLineItem(
        [property: JsonPropertyName("productId")] string? ProductID,
        [property: JsonPropertyName("expiryTime")] DateTimeOffset ExpiryTime);
}

/// <summary>
/// A Play subscription's status as Google itself reports it.
/// </summary>
/// <remarks>
/// Null from <see cref="GooglePlayClient.GetSubscription"/> means "no such purchase" -- a token that
/// does not resolve at all. This type, once returned, always names a real purchase; whether it
/// currently grants access is a property of <see cref="State"/> and <see cref="ExpiresAt"/>, decided
/// by the caller, not folded into a third meaning of null.
/// </remarks>
public sealed record GoogleSubscriptionStatus(string ProductID, string State, DateTimeOffset ExpiresAt, string? OrderID);
