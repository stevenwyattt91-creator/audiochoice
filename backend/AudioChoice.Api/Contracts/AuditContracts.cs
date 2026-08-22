namespace AudioChoice.Api.Contracts;

public sealed record InternalAccess(Guid UserID, string Email, string DisplayName, string Role, bool Active);
public sealed record AuditCategory(Guid ID, string Name, string? Description);
public sealed record AuditAssignmentSummary(Guid ID, string Title, string? Author, string? Edition,
    string Fingerprint, int CandidateCount, int ReviewedCount, string Status, DateTimeOffset? CompletedAt,
    decimal? CompensationAmount, string PaymentStatus, string ReviewFocus = "All detected events",
    string ReviewMediaStatus = "waiting_for_source");
public sealed record AuditCandidate(Guid ID, double StartSeconds, double EndSeconds, Guid CategoryID,
    double Confidence, string SafeDescription, string StableKey, double ListenFromSeconds, double ListenToSeconds);
public sealed record AuditWorkspace(AuditAssignmentSummary Assignment, IReadOnlyList<AuditCategory> Categories,
    IReadOnlyList<AuditCandidate> Candidates, IReadOnlyList<AuditDecisionRecord> Decisions, bool ReviewMediaAvailable);
public sealed record AuditDecisionRequest(string Decision, Guid? CorrectedCategoryID,
    double? CorrectedStartSeconds, double? CorrectedEndSeconds, string? Notes);
public sealed record AuditDecisionRecord(Guid ID, Guid CandidateID, string Decision, Guid? CorrectedCategoryID,
    double? CorrectedStartSeconds, double? CorrectedEndSeconds, string? Notes, DateTimeOffset UpdatedAt);
public sealed record CreateAuditAssignmentRequest(Guid ScanResultID, Guid? AuditorID, bool BlindQC,
    decimal? CompensationAmount);
public sealed record InternalUserRequest(string Email, string Password, string DisplayName, string Role);
public sealed record InternalUserStatusRequest(bool Active);
public sealed record AuditorEarnings(decimal ThisWeek, decimal AwaitingApproval, decimal ApprovedUnpaid, decimal PaidThisWeek);
public sealed record FocusedAuditEstimate(int EligibleEvents, int ReviewWindows, int LongReviewWindows,
    decimal BasePay, decimal WindowPay, decimal LongSceneBonus, decimal EstimatedPay,
    decimal EstimatedPayWithMajorCorrection, decimal MaximumPay);
public sealed record AuditReviewSource(string ObjectName, string OriginalFileName, string ContentType, long FileSize);
public sealed record AuditReviewClip(string ObjectName, double StartSeconds, double EndSeconds);
public sealed record AuditReviewSourceUploadRequest(string FileName, string ContentType, long FileSize);
public sealed record AuditReviewSourceUploadAuthorization(Uri UploadURL, string Method, IReadOnlyDictionary<string, string> Headers, DateTimeOffset ExpiresAt);
