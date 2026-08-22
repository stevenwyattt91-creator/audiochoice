namespace AudioChoice.Api.Contracts;

public enum CloudScanStatus
{
    Available,
    UploadRequired,
    Queued,
    Processing,
    Completed,
    Failed
}

public static class ScanProcessingLanes
{
    public const string AzureOpenAI = "azure-openai";
    public const string IOSBetaLambda = "ios-beta-lambda";
}

public sealed record BookFingerprint(
    int Version,
    string Sha256,
    long FileSize,
    double? Duration,
    string FileType,
    string? WorkTitle,
    string? Author,
    string? SeriesTitle,
    int? SeriesNumber,
    string? EditionType,
    int? PartNumber,
    int? TotalParts);

public sealed record ScanEvent(
    Guid Id,
    double StartTime,
    double EndTime,
    Guid CategoryID,
    Guid GroupID,
    Guid EventID,
    double Confidence,
    string StableKey = "",
    string SafeDescription = "Content event detected",
    string? AggregateKey = null,
    string? AggregateDisplay = null);

public sealed record ScanResult(
    IReadOnlyList<ScanEvent> Events,
    DateTimeOffset ScanDate,
    string ScannerVersion);

public sealed record CloudScanRequest(
    BookFingerprint Fingerprint,
    string? CurrentScannerVersion);

public sealed record CloudScanResponse(
    CloudScanStatus Status,
    Guid? ScanID = null,
    ScanResult? Result = null,
    string TaxonomyVersion = "2.0",
    int ProgressPercent = 0,
    string? ProgressStage = null,
    int CompletedChunks = 0,
    int TotalChunks = 0,
    int PercentComplete = 0);

public sealed record CloudUploadAuthorizationRequest(
    BookFingerprint Fingerprint,
    string FileName,
    string ContentType,
    long FileSize);

public sealed record EmbeddedCoverUploadRequest(
    BookFingerprint Fingerprint,
    string ContentType,
    string Base64Data);

public sealed record CloudUploadAuthorizationResponse(
    Guid UploadID,
    Uri UploadURL,
    string Method,
    IReadOnlyDictionary<string, string> Headers,
    DateTimeOffset ExpiresAt);

public sealed record CloudScanJobSubmissionRequest(
    Guid UploadID,
    BookFingerprint Fingerprint);

public sealed record AdminReanalysisRequest(
    Guid OwnerUserID,
    BookFingerprint Fingerprint);

public sealed record AdminTranscriptInfo(
    BookFingerprint Fingerprint,
    int SegmentCount,
    bool IsComplete,
    string TranscriptionModel,
    DateTimeOffset CreatedAt);

public sealed record AdminEditionMetadataRequest(
    BookFingerprint Fingerprint,
    string WorkTitle,
    string? Author,
    string? SeriesTitle,
    int? SeriesNumber,
    string? EditionType,
    int? PartNumber,
    int? TotalParts,
    double? Duration);

public sealed record ExploreCatalogBook(
    string CatalogID,
    string Title,
    string? Author,
    string? SeriesTitle,
    int? SeriesNumber,
    string? EditionType,
    double? Duration,
    string FileType,
    DateTimeOffset ScanDate,
    string ScannerVersion,
    int EventCount,
    IReadOnlyList<Guid> DetectedGroupIDs,
    string? CoverImageURL,
    string? Description,
    Uri PurchaseURL,
    string PurchaseProvider,
    bool PurchaseVerified);
