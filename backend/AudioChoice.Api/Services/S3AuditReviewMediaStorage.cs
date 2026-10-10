using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

/// <summary>
/// Auditor review media -- the M4B under review and the short clips cut from it -- held in S3.
/// The AWS counterpart to <c>BlobAuditReviewMediaStorage</c>.
/// </summary>
/// <remarks>
/// The Azure implementation calls <c>CreateIfNotExistsAsync</c> on nearly every write, because a
/// fresh environment has no container until the first upload. S3 has no equivalent step: the bucket
/// is created by the CloudFormation stack and a prefix is not a thing that exists or does not, so
/// those calls simply have no counterpart here rather than being omitted by oversight.
/// </remarks>
public sealed class S3AuditReviewMediaStorage(
    IAmazonS3 client,
    TemporaryAudioStorageOptions options) : IAuditReviewMediaStorage
{
    public bool IsAvailable => true;

    private string Prefix => string.IsNullOrWhiteSpace(options.AuditReviewContainerName)
        ? "audit-review-media"
        : options.AuditReviewContainerName;

    public async Task<StoredAuditReviewMedia> StoreSource(
        Guid assignmentID,
        string fileName,
        string contentType,
        Stream input,
        CancellationToken cancellationToken)
    {
        var objectName = SourceName(assignmentID);
        await client.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = options.S3BucketName,
                Key = Key(objectName),
                InputStream = input,
                ContentType = contentType
            },
            cancellationToken);
        var metadata = await Metadata(Key(objectName), cancellationToken);
        return new(objectName, metadata?.ContentLength ?? 0);
    }

    public Task<AuditReviewSourceUploadAuthorization?> CreateSourceUploadAuthorization(
        Guid assignmentID,
        string contentType,
        CancellationToken cancellationToken)
    {
        // Thirty minutes, as on Azure.
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        var url = client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = options.S3BucketName,
            Key = Key(SourceName(assignmentID)),
            Verb = HttpVerb.PUT,
            Expires = expiresAt.UtcDateTime,
            ContentType = contentType
        });
        return Task.FromResult<AuditReviewSourceUploadAuthorization?>(new AuditReviewSourceUploadAuthorization(
            new Uri(url),
            "PUT",
            new Dictionary<string, string> { ["Content-Type"] = contentType },
            expiresAt));
    }

    public async Task<StoredAuditReviewMedia?> VerifySource(Guid assignmentID, CancellationToken cancellationToken)
    {
        var objectName = SourceName(assignmentID);
        var metadata = await Metadata(Key(objectName), cancellationToken);
        if (metadata is null || metadata.ContentLength <= 0) return null;
        return new StoredAuditReviewMedia(objectName, metadata.ContentLength);
    }

    public async Task<MaterializedAudio> MaterializeSource(string objectName, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"audiochoice-audit-{Guid.NewGuid():N}.m4b");
        try
        {
            using var response = await client.GetObjectAsync(
                new GetObjectRequest { BucketName = options.S3BucketName, Key = Key(objectName) },
                cancellationToken);
            await response.WriteResponseStreamToFileAsync(path, false, cancellationToken);
            return new MaterializedAudio(path, true);
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }

    public async Task<StoredAuditReviewMedia> StoreClip(
        Guid assignmentID,
        Guid eventID,
        Stream input,
        CancellationToken cancellationToken)
    {
        var objectName = $"{assignmentID:N}/clips/{eventID:N}.m4a";
        await client.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = options.S3BucketName,
                Key = Key(objectName),
                InputStream = input,
                ContentType = "audio/mp4"
            },
            cancellationToken);
        var metadata = await Metadata(Key(objectName), cancellationToken);
        return new(objectName, metadata?.ContentLength ?? 0);
    }

    public async Task<Uri?> CreateReadAuthorization(string objectName, CancellationToken cancellationToken)
    {
        var key = Key(objectName);
        if (await Metadata(key, cancellationToken) is null) return null;
        // Five minutes, as on Azure: long enough to start playing a clip, short enough that the
        // URL is not worth passing around.
        var url = client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = options.S3BucketName,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.AddMinutes(5)
        });
        return new Uri(url);
    }

    public async Task DeletePrefix(Guid assignmentID, CancellationToken cancellationToken)
    {
        // Listed a page at a time and deleted in batches. ListObjectsV2 returns at most 1000 keys
        // per call, so the continuation token is not optional for an assignment with many clips.
        var request = new ListObjectsV2Request
        {
            BucketName = options.S3BucketName,
            Prefix = $"{Prefix}/{assignmentID:N}/"
        };
        while (true)
        {
            var listing = await client.ListObjectsV2Async(request, cancellationToken);
            if (listing.S3Objects.Count > 0)
            {
                await client.DeleteObjectsAsync(
                    new DeleteObjectsRequest
                    {
                        BucketName = options.S3BucketName,
                        Objects = listing.S3Objects
                            .Select(item => new KeyVersion { Key = item.Key })
                            .ToList()
                    },
                    cancellationToken);
            }
            if (!listing.IsTruncated.GetValueOrDefault()) return;
            request.ContinuationToken = listing.NextContinuationToken;
        }
    }

    private async Task<GetObjectMetadataResponse?> Metadata(string key, CancellationToken cancellationToken)
    {
        try
        {
            return await client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = options.S3BucketName, Key = key },
                cancellationToken);
        }
        catch (AmazonS3Exception failure) when (failure.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static string SourceName(Guid assignmentID) => $"{assignmentID:N}/source.m4b";

    /// <summary>
    /// The full bucket key for an object name.
    /// </summary>
    /// <remarks>
    /// Object names are stored in the database and handed back to <see cref="MaterializeSource"/>
    /// and <see cref="CreateReadAuthorization"/> later, so they must stay exactly the values the
    /// Azure implementation used -- container-relative, with no prefix. The prefix is applied here
    /// and only here.
    /// </remarks>
    private string Key(string objectName) => $"{Prefix}/{objectName}";
}
