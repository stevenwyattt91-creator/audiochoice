namespace AudioChoice.Api.Services;

public sealed class TemporaryAudioStorageOptions
{
    public bool BlobEnabled { get; init; }
    public string StorageAccountName { get; init; } = string.Empty;
    public string ContainerName { get; init; } = "temporary-audio";
    public int UploadAuthorizationMinutes { get; init; } = 15;
    public int MaximumRetentionHours { get; init; } = 24;
    public string CompanionTransferContainerName { get; init; } = "companion-transfers";
    public string AuditReviewContainerName { get; init; } = "audit-review-media";
    /// <summary>Stores private scan transcripts in Blob so independent workers can share them.</summary>
    public bool BlobTranscriptEnabled { get; init; }
    public string TranscriptContainerName { get; init; } = "private-transcripts";
}
