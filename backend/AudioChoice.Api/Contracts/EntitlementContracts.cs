namespace AudioChoice.Api.Contracts;

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
public sealed record EntitlementGrantRequest(
    string Plan,
    string Source,
    DateTimeOffset? ExpiresAt,
    string? ExternalReference = null);
