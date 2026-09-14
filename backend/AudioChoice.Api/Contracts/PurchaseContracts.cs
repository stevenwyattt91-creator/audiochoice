namespace AudioChoice.Api.Contracts;

/// <summary>
/// A StoreKit2 transaction the client wants verified and turned into access, submitted right after
/// <c>Transaction.updates</c>/a completed purchase.
/// </summary>
/// <remarks>
/// <see cref="SignedTransactionInfo"/> is a JWS -- Apple's signature, not the client's claim -- so
/// this server never trusts anything the client says about what was bought. It re-derives the
/// product and expiry from the signed payload itself.
/// </remarks>
public sealed record AppleTransactionRequest(string SignedTransactionInfo);

/// <summary>
/// An acknowledged Play Billing purchase the client wants verified and turned into access.
/// </summary>
/// <remarks>
/// The token alone is looked up against the Play Developer API, which is Google's own record of
/// the purchase -- the client's <paramref name="ProductID"/> is used only to route the request to
/// the matching Play Developer API call, and is re-checked against what that response says was
/// actually purchased.
/// </remarks>
public sealed record GooglePurchaseRequest(string ProductID, string PurchaseToken);

public sealed record PurchaseVerificationResult(
    bool Verified,
    AccountAccessResponse? Access,
    string? Error,
    /// <summary>
    /// What was actually bought, read back out of the store's own signed payload. Null on failure.
    /// </summary>
    /// <remarks>
    /// Defaulted last so every existing positional construction keeps compiling. Exists so the
    /// endpoint can describe a purchase without decoding the JWS a second time -- the verifier has
    /// already done that, and it is the only party that legitimately can.
    /// </remarks>
    VerifiedPurchaseDetail? Detail = null);

/// <summary>
/// The parts of a verified purchase worth reporting: which product, under which offer, from where.
/// </summary>
public sealed record VerifiedPurchaseDetail(
    string Store,
    string? ProductID,
    string? OfferIdentifier,
    string? OfferDescription,
    string? Storefront,
    DateTimeOffset? ExpiresAt);
