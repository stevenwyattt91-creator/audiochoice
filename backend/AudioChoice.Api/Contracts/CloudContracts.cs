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

/// <summary>
/// A known audiobook edition and whether timing data exists for it.
/// </summary>
/// <remarks>
/// Unlike <see cref="AdminTranscriptInfo"/> this lists editions that have *no*
/// transcript, which is what makes a missing one findable in the first place.
/// </remarks>
public sealed record AdminEditionInfo(
    BookFingerprint Fingerprint,
    bool HasTranscript,
    int SegmentCount);

/// <summary>
/// Timing data produced outside the scan pipeline, to be stored against an
/// existing edition.
/// </summary>
public sealed record AdminTranscriptIngestRequest(
    BookFingerprint Fingerprint,
    Processing.PrivateTranscript Transcript);

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
    bool PurchaseVerified,
    /// <summary>
    /// The retail product identifier for this recording, when one is known.
    /// </summary>
    /// <remarks>
    /// Carried so the catalogue can recognise two copies of one recording as the same
    /// entry. Titles cannot do that on their own: the same edition arrives spelled several
    /// ways depending on who tagged the file.
    /// </remarks>
    string? ProductIdentifier = null);

/// <summary>
/// One scanned edition as an administrator sees it, published or not.
/// </summary>
/// <remarks>
/// The catalogue listing shows only what listeners can see, which is no use for managing it:
/// an entry that has been hidden, or that is being withheld because it names no book, is
/// invisible in exactly the view you would use to find and fix it.
/// </remarks>
public sealed record ExploreCatalogAdminEntry(
    ExploreCatalogBook Book,
    /// <summary>False once an administrator has hidden this edition.</summary>
    bool IsPublished,
    /// <summary>Whether the edition names a book well enough to be listed.</summary>
    bool IsPublishable,
    /// <summary>Why listeners cannot see this entry, or null when they can.</summary>
    string? WithheldReason);
