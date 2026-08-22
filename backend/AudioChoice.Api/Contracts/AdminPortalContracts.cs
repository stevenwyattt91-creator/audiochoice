namespace AudioChoice.Api.Contracts;

public sealed record AdminDashboardSummary(
    int ScannedEditionCount,
    int AwaitingApprovalCount,
    int ReadyToPayCount,
    decimal ReadyToPayAmount,
    int ActiveAuditorCount);

public sealed record AdminCatalogEditionSummary(
    Guid EditionID,
    Guid ScanResultID,
    BookFingerprint Fingerprint,
    DateTimeOffset ScanDate,
    string ScannerVersion,
    int EventCount,
    bool ExplorePublished,
    bool HasCoverArt,
    bool HasTranscript);

public sealed record AdminAuditPayment(
    Guid AssignmentID,
    Guid? AuditorID,
    string? AuditorName,
    string? AuditorEmail,
    string Title,
    string? Edition,
    string Status,
    decimal Amount,
    string PaymentStatus,
    DateTimeOffset? CompletedAt,
    DateOnly? PaymentDate,
    string? PaymentNote);

public sealed record AuditPaymentRequest(string? Note);
public sealed record AuditCompensationRequest(decimal Amount);
