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
    /// Serves direct uploads from S3 instead of Azure Blob Storage.
    /// </summary>
    /// <remarks>
    /// A separate switch rather than a replacement for <see cref="BlobEnabled"/>, so an existing
    /// Azure deployment keeps behaving exactly as before and a rollback is a configuration change
    /// rather than a revert. <c>Program.cs</c> prefers S3 when both are set.
    ///
    /// The four Azure containers become four key prefixes inside one bucket. One bucket is cheaper
    /// and simpler to grant, and the container names above are reused verbatim as the prefixes, so
    /// object layout stays recognisable across the two providers.
    /// </remarks>
    public bool S3Enabled { get; init; }

    /// <summary>The single bucket holding every prefix. Required when <see cref="S3Enabled"/>.</summary>
    public string S3BucketName { get; init; } = string.Empty;

    /// <summary>
    /// Empty means the SDK resolves the region itself, from <c>AWS_REGION</c> or the task's own
    /// configuration -- the same way the existing Polly and Bedrock clients already do.
    /// </summary>
    public string S3Region { get; init; } = string.Empty;

    /// <summary>Stores private scan transcripts in S3 so independent workers can share them.</summary>
    public bool S3TranscriptEnabled { get; init; }

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
