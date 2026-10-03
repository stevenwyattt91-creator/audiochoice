namespace AudioChoice.Api.Services;

public sealed class TemporaryAudioStorageOptions
{
    public bool BlobEnabled { get; init; }
    public string StorageAccountName { get; init; } = string.Empty;
    public string ContainerName { get; init; } = "temporary-audio";
    // Mobile uploads may take a while on real-world connections. The client
    // still verifies size/hash and the authorization remains scoped to one user.
    public int UploadAuthorizationMinutes { get; init; } = 120;
    public int MaximumRetentionHours { get; init; } = 24;
    public string CompanionTransferContainerName { get; init; } = "companion-transfers";
    public string AuditReviewContainerName { get; init; } = "audit-review-media";
    /// <summary>Stores private scan transcripts in Blob so independent workers can share them.</summary>
    public bool BlobTranscriptEnabled { get; init; }
    public string TranscriptContainerName { get; init; } = "private-transcripts";

    /// <summary>
    /// Stores private scan transcripts in S3 instead of Blob, for the same sharing reason.
    /// </summary>
    /// <remarks>
    /// Its own flag rather than a provider enum, so both can be configured at once during a move:
    /// whichever is enabled wins, and turning this on while Blob is still set is a deliberate
    /// mistake worth refusing at startup rather than resolving silently in favour of one.
    /// </remarks>
    public bool S3TranscriptEnabled { get; init; }

    public string TranscriptBucketName { get; init; } = string.Empty;

    /// <summary>
    /// The region the transcript bucket lives in. Empty lets the SDK resolve it the way the Polly
    /// and Bedrock clients already do -- from the environment or the instance's own configuration.
    /// </summary>
    public string S3Region { get; init; } = string.Empty;
}
