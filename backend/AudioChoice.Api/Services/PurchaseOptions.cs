namespace AudioChoice.Api.Services;

/// <summary>
/// Server-side switches and credentials for verifying store subscription purchases.
/// </summary>
/// <remarks>
/// Every field defaults to empty/off, so deploying this code changes nothing about a running
/// environment until store products and credentials exist and are configured. Both endpoints that
/// consume this (<c>POST /v1/purchases/apple</c> and <c>POST /v1/purchases/google</c>) check the
/// relevant "Enabled" flag before doing anything and answer 503 rather than attempting a
/// verification that has nowhere to send its request -- a missing credential should look like "not
/// available yet," not like a purchase that failed.
/// </remarks>
public sealed class PurchaseOptions
{
    /// <summary>Whether Apple purchase verification is configured and reachable.</summary>
    public bool AppleEnabled { get; init; }

    /// <summary>
    /// The app's bundle ID, checked against a decoded transaction's own <c>bundleId</c> claim.
    /// </summary>
    /// <remarks>
    /// Apple signs transactions for every app that uses their signing keys, not just this one. Without
    /// this check, a signed transaction from an unrelated app would decode cleanly and grant access
    /// here.
    /// </remarks>
    public string AppleBundleID { get; init; } = string.Empty;

    /// <summary>
    /// The subscription product identifiers this server recognizes, comma-separated.
    /// </summary>
    /// <remarks>
    /// A decoded, signature-verified transaction is still just Apple vouching for "this product was
    /// bought" -- it says nothing about whether that product is the audiobook subscription this
    /// server grants access for. Checked case-sensitively, since App Store Connect product IDs are.
    /// </remarks>
    public string AppleProductIDs { get; init; } = string.Empty;
    public IReadOnlyList<string> AppleProductIDList => Split(AppleProductIDs);

    /// <summary>
    /// The App Store Server API key ID, issuer ID, and private key (.p8 contents), used to call
    /// Apple's API for transaction history and to verify the notification signing chain against
    /// Apple's root certificate.
    /// </summary>
    /// <remarks>
    /// Created under App Store Connect &gt; Users and Access &gt; Integrations &gt; In-App Purchase.
    /// The private key is a PEM-format .p8 file's contents, not a file path -- read once at startup
    /// from configuration/secrets, never written to disk by this server.
    /// </remarks>
    public string AppleKeyID { get; init; } = string.Empty;
    public string AppleIssuerID { get; init; } = string.Empty;
    public string AppleSigningKeyPem { get; init; } = string.Empty;

    /// <summary>
    /// The shared secret Apple includes in the URL when configuring App Store Server Notifications
    /// V2, checked so this endpoint cannot be triggered by anyone who merely knows its URL.
    /// </summary>
    /// <remarks>
    /// This is a defense in depth measure -- the notification payload itself is signature-verified
    /// against Apple's certificate chain regardless -- but a wrong or missing token is rejected before
    /// that more expensive check runs.
    /// </remarks>
    public string AppleNotificationToken { get; init; } = string.Empty;

    /// <summary>Whether Google Play purchase verification is configured and reachable.</summary>
    public bool GoogleEnabled { get; init; }

    /// <summary>The app's Play Store package name, e.g. <c>com.audiochoice.mobile</c>.</summary>
    public string GooglePackageName { get; init; } = string.Empty;

    /// <summary>The subscription product identifiers this server recognizes, comma-separated.</summary>
    public string GoogleProductIDs { get; init; } = string.Empty;
    public IReadOnlyList<string> GoogleProductIDList => Split(GoogleProductIDs);

    /// <summary>
    /// A Google Cloud service account's JSON key, granted access to this app in Play Console under
    /// Setup &gt; API access, used to call the Play Developer API and to obtain the OAuth token
    /// Google's Pub/Sub push subscription needs.
    /// </summary>
    public string GoogleServiceAccountJson { get; init; } = string.Empty;

    /// <summary>
    /// A shared secret placed in the push endpoint URL configured on this server's own Pub/Sub push
    /// subscription (Cloud Console, not Play Console), checked so the notification endpoint cannot
    /// be triggered by anyone who merely knows its URL.
    /// </summary>
    public string GoogleNotificationToken { get; init; } = string.Empty;

    private static IReadOnlyList<string> Split(string value) => value
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}
