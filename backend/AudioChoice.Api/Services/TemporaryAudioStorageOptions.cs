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
    ///
    /// Kept as its own dedicated bucket (<see cref="TranscriptBucketName"/>) rather than folded
    /// into the shared media bucket's prefixes below: the transcript migration ran and completed
    /// against a standalone bucket before the rest of this file's S3 fields existed, so changing
    /// its layout now would orphan every transcript already written under this scheme.
    /// </remarks>
    public bool S3TranscriptEnabled { get; init; }

    public string TranscriptBucketName { get; init; } = string.Empty;

    /// <summary>
    /// The region the transcript bucket lives in. Empty lets the SDK resolve it the way the Polly
    /// and Bedrock clients already do -- from the environment or the instance's own configuration.
    /// </summary>
    public string S3Region { get; init; } = string.Empty;

    /// <summary>
    /// Serves direct uploads, companion transfers, and audit review media from S3 instead of
    /// Azure Blob Storage.
    /// </summary>
    /// <remarks>
    /// A separate switch rather than a replacement for <see cref="BlobEnabled"/>, so an existing
    /// Azure deployment keeps behaving exactly as before and a rollback is a configuration change
    /// rather than a revert. <c>Program.cs</c> prefers S3 when both are set.
    ///
    /// The three remaining Azure containers (temporary audio, companion transfers, audit review
    /// media) become three key prefixes inside one shared bucket, distinct from the transcript
    /// bucket above. One bucket is cheaper and simpler to grant, and the container names above
    /// are reused verbatim as the prefixes, so object layout stays recognisable across the two
    /// providers.
    /// </remarks>
    public bool S3Enabled { get; init; }

    /// <summary>The single bucket holding every prefix except transcripts. Required when <see cref="S3Enabled"/>.</summary>
    public string S3BucketName { get; init; } = string.Empty;

    /// <summary>
    /// Optional endpoint override, for pointing the S3 client at MinIO or LocalStack in
    /// development. Empty means the real AWS endpoint for the resolved region.
    /// </summary>
    public string S3ServiceURL { get; init; } = string.Empty;

    /// <summary>
    /// Addresses the bucket as a path segment rather than a subdomain. Required by MinIO and
    /// LocalStack; leave off for AWS, which prefers virtual-hosted style.
    /// </summary>
    public bool S3ForcePathStyle { get; init; }
}
