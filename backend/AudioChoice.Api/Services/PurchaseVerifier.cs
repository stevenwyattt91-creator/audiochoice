using System.Text.Json;
using System.Text.Json.Serialization;
using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

/// <summary>
/// Turns a client's claimed store purchase into a verified, server-recorded entitlement.
/// </summary>
/// <remarks>
/// Neither path trusts the client for anything beyond "here is a token, please check it" -- the
/// product, the expiry, and whether the purchase is even genuine all come back out of Apple's or
/// Google's own signed/authenticated response. This is also the only place in the codebase allowed
/// to grant <see cref="AccountPlans.Premium"/>; see the remarks on that constant.
/// </remarks>
public sealed class PurchaseVerifier(
    PurchaseOptions options,
    IEntitlementStore entitlements,
    GooglePlayClient googlePlay,
    ILogger<PurchaseVerifier> logger)
{
    public Task<PurchaseVerificationResult> VerifyApple(
        Guid userID, AppleTransactionRequest request, CancellationToken cancellationToken)
    {
        if (!options.AppleEnabled)
        {
            return Task.FromResult(new PurchaseVerificationResult(false, null,
                "Apple purchase verification is not yet configured on this server."));
        }

        using var payload = AppleJWS.VerifyAndDecode(request.SignedTransactionInfo);
        if (payload is null)
        {
            logger.LogWarning("Rejected an Apple transaction whose signature did not verify.");
            return Task.FromResult(new PurchaseVerificationResult(false, null, "That purchase could not be verified with Apple."));
        }

        AppleTransactionPayload? transaction;
        try
        {
            transaction = JsonSerializer.Deserialize<AppleTransactionPayload>(payload.RootElement.GetRawText());
        }
        catch (JsonException)
        {
            return Task.FromResult(new PurchaseVerificationResult(false, null, "That purchase could not be read."));
        }
        if (transaction?.TransactionID is null || transaction.ProductID is null)
        {
            return Task.FromResult(new PurchaseVerificationResult(false, null, "That purchase could not be read."));
        }

        if (!string.IsNullOrWhiteSpace(options.AppleBundleID) &&
            !string.Equals(transaction.BundleID, options.AppleBundleID, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "Rejected a verified Apple transaction naming a different app bundle ({BundleID}).",
                transaction.BundleID);
            return Task.FromResult(new PurchaseVerificationResult(false, null, "That purchase belongs to a different app."));
        }
        if (options.AppleProductIDList.Count > 0 &&
            !options.AppleProductIDList.Contains(transaction.ProductID, StringComparer.Ordinal))
        {
            return Task.FromResult(new PurchaseVerificationResult(false, null, "That product is not an AudioChoice subscription."));
        }

        // A revocation date means Apple refunded or otherwise reversed this specific transaction.
        // Treated the same as an expiry rather than as "not purchased" -- the purchase happened, it
        // simply no longer grants access, and Grant with an already-past expiry records exactly that
        // without a separate revoke call.
        var expiresAt = transaction.RevocationDate ?? transaction.ExpiresDate;
        if (expiresAt is null || expiresAt <= DateTimeOffset.UtcNow)
        {
            return Task.FromResult(new PurchaseVerificationResult(false, null, "That subscription is not currently active."));
        }

        var access = entitlements.Grant(userID, new EntitlementGrantRequest(
            Plan: AccountPlans.Premium,
            Source: "apple",
            ExpiresAt: expiresAt,
            ExternalReference: transaction.OriginalTransactionID ?? transaction.TransactionID));
        return Task.FromResult(new PurchaseVerificationResult(true, access, null));
    }

    public async Task<PurchaseVerificationResult> VerifyGoogle(
        Guid userID, GooglePurchaseRequest request, CancellationToken cancellationToken)
    {
        if (!options.GoogleEnabled)
        {
            return new PurchaseVerificationResult(false, null,
                "Google Play purchase verification is not yet configured on this server.");
        }

        var subscription = await googlePlay.GetSubscription(request.PurchaseToken, cancellationToken);
        if (subscription is null)
        {
            logger.LogWarning("Rejected a Google Play purchase token that did not resolve to a purchase.");
            return new PurchaseVerificationResult(false, null, "That purchase could not be verified with Google Play.");
        }

        if (options.GoogleProductIDList.Count > 0 &&
            !options.GoogleProductIDList.Contains(subscription.ProductID, StringComparer.Ordinal))
        {
            return new PurchaseVerificationResult(false, null, "That product is not an AudioChoice subscription.");
        }
        if (!string.Equals(subscription.ProductID, request.ProductID, StringComparison.Ordinal))
        {
            // The client's claimed product must match what Google's own record of the token says was
            // purchased -- otherwise a token for one product could be submitted alongside a claim of
            // a different, perhaps higher, entitlement.
            logger.LogWarning(
                "Rejected a Google Play purchase claiming product {Claimed} but Google reports {Actual}.",
                request.ProductID, subscription.ProductID);
            return new PurchaseVerificationResult(false, null, "That purchase does not match the product submitted.");
        }

        var grantableStates = new[] { "SUBSCRIPTION_STATE_ACTIVE", "SUBSCRIPTION_STATE_IN_GRACE_PERIOD" };
        if (!grantableStates.Contains(subscription.State, StringComparer.Ordinal) ||
            subscription.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return new PurchaseVerificationResult(false, null, "That subscription is not currently active.");
        }

        // Best-effort: a failed acknowledgment must not block granting access the purchase already
        // earned. If it never succeeds Google auto-refunds after three days, which the next renewal
        // notification (once server notifications are wired to a real endpoint) would then reflect.
        try { await googlePlay.Acknowledge(subscription.ProductID, request.PurchaseToken, cancellationToken); }
        catch (Exception error) { logger.LogError(error, "Could not acknowledge a Google Play purchase."); }

        var access = entitlements.Grant(userID, new EntitlementGrantRequest(
            Plan: AccountPlans.Premium,
            Source: "google",
            ExpiresAt: subscription.ExpiresAt,
            ExternalReference: subscription.OrderID ?? request.PurchaseToken));
        return new PurchaseVerificationResult(true, access, null);
    }

    private sealed record AppleTransactionPayload(
        [property: JsonPropertyName("transactionId")] string? TransactionID,
        [property: JsonPropertyName("originalTransactionId")] string? OriginalTransactionID,
        [property: JsonPropertyName("bundleId")] string? BundleID,
        [property: JsonPropertyName("productId")] string? ProductID,
        [property: JsonPropertyName("expiresDate")] long? ExpiresDateMillis,
        [property: JsonPropertyName("revocationDate")] long? RevocationDateMillis)
    {
        public DateTimeOffset? ExpiresDate => ExpiresDateMillis is { } millis
            ? DateTimeOffset.FromUnixTimeMilliseconds(millis) : null;
        public DateTimeOffset? RevocationDate => RevocationDateMillis is { } millis
            ? DateTimeOffset.FromUnixTimeMilliseconds(millis) : null;
    }
}
