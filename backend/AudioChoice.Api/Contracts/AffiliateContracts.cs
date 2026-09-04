namespace AudioChoice.Api.Contracts;

/// <summary>
/// An affiliate's referral code plus how many accounts it has attributed.
/// </summary>
public sealed record AffiliateSummary(
    Guid ID,
    string Code,
    string Label,
    string? Email,
    bool Active,
    DateTimeOffset CreatedAt,
    int ReferralCount);

/// <summary>One account attributed to an affiliate's code.</summary>
public sealed record AffiliateReferral(
    Guid UserID,
    string Email,
    string DisplayName,
    DateTimeOffset AttributedAt);

public sealed record AffiliateDetail(
    AffiliateSummary Affiliate,
    IReadOnlyList<AffiliateReferral> Referrals);

public sealed record CreateAffiliateRequest(string Label, string? Email);

public sealed record AffiliateActiveRequest(bool Active);

/// <summary>
/// Whether a referral code is currently usable, for a client to check before
/// showing it as accepted.
/// </summary>
/// <remarks>
/// Deliberately thin: a label only, nothing that identifies the affiliate by
/// name or email to whoever is typing a code into the app.
/// </remarks>
public sealed record ReferralCodeCheck(bool Valid);
