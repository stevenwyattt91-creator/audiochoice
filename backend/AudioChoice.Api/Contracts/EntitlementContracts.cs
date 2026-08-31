namespace AudioChoice.Api.Contracts;

/// <summary>
/// Plan names carried by <see cref="AccountAccessResponse.Plan"/>.
/// </summary>
public static class AccountPlans
{
    /// <summary>No entitlement. The default for a new account.</summary>
    public const string Free = "free";

    /// <summary>
    /// A beta tester, given full access at no charge permanently.
    /// </summary>
    /// <remarks>
    /// Free rather than discounted, and deliberately so. A reduced price would mean a second
    /// subscription product in both stores, logic choosing which one to offer, and a server check
    /// that a cheap receipt belongs to a real founder -- because the product exists in the store
    /// whether or not the app shows it, so without that check anyone could buy it. That is a
    /// permanent attack surface and a permanent maintenance cost, for a handful of accounts.
    ///
    /// Granted with no expiry, which <see cref="IEntitlementStore"/> already treats as never
    /// expiring and prefers over any dated grant. So a founder who later subscribes by accident is
    /// still read as a founder rather than downgraded to what they paid for.
    /// </remarks>
    public const string Founder = "founder";

    /// <summary>Whether a plan name means the account is never charged.</summary>
    public static bool IsComplimentary(string? plan) =>
        string.Equals(plan, Founder, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Account-level access used by every AudioChoice client. Store receipts will be
/// verified server-side and recorded here; clients never decide access themselves.
/// </summary>
public sealed record AccountAccessResponse(
    bool IsActive,
    string Plan,
    string Source,
    DateTimeOffset? ExpiresAt,
    bool CanUseFilters,
    bool CanUseCompanion);

/// <summary>
/// Restricted to the configured administrator token. Intended for beta/founding
/// grants and support corrections, never for client-side purchase verification.
/// </summary>
/// <summary>
/// Marks an account as a founder by email address.
/// </summary>
/// <remarks>
/// Separate from <see cref="EntitlementGrantRequest"/> on purpose. That one takes a plan, a source
/// and an expiry, so granting a founder by hand means getting three fields right and a mistyped
/// expiry silently produces access that lapses. This takes only the address; the plan, the source
/// and the absence of an expiry are not the caller's to choose.
/// </remarks>
public sealed record FounderGrantRequest(string Email);

public sealed record EntitlementGrantRequest(
    string Plan,
    string Source,
    DateTimeOffset? ExpiresAt,
    string? ExternalReference = null);
