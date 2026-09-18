using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

/// <summary>
/// Direct-upload temporary audio held in S3, the AWS counterpart to
/// <c>BlobTemporaryAudioStorage</c>.
/// </summary>
/// <remarks>
/// Deliberately a sibling of the Azure implementation rather than a replacement. Both satisfy
/// <see cref="ITemporaryAudioStorage"/> and <c>Program.cs</c> picks one, so moving providers is a
/// configuration change and moving back is the same change in reverse.
///
/// Two differences from the Azure path are worth knowing, because they reach past this class:
///
/// The returned header set carries no <c>x-ms-blob-type</c>. That header was never cosmetic -- the
/// Android client branches on its presence to choose between a chunked Azure block-blob upload and a
/// single PUT (see <c>AudioChoiceApi.kt</c>), so its absence is what makes an already-shipped
/// Android build take the plain single-request path against a presigned URL. iOS applies whatever
/// headers it is handed and always sends one PUT, so it needs nothing.
///
/// A single PUT to S3 is capped at 5 GiB while <c>AudioChoice__MaximumUploadBytes</c> allows 20 GiB.
/// Anything above the cap needs multipart upload, which the shipped clients cannot do. Audiobooks
/// that large are rare, and the failure is a clean rejection from S3 rather than corruption, but it
/// is a real gap against the Azure path, which chunked.
/// </remarks>
public sealed class S3TemporaryAudioStorage(
    IAmazonS3 client,
    TemporaryAudioStorageOptions options) : ITemporaryAudioStorage
{
    public bool UsesDirectUpload => true;

    private string Prefix => string.IsNullOrWhiteSpace(options.ContainerName)
        ? "temporary-audio"
        : options.ContainerName;

    public Task<CloudUploadAuthorizationResponse?> CreateDirectUploadAuthorization(
        UploadRecord upload,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        // Signed with whatever credentials the task role currently holds. Those are temporary and
        // rotate, and a presigned URL dies with the credentials that signed it even if its own
        // expiry is later -- so a very long authorization window is a promise this cannot always
        // keep. The client's remedy is the same as for any expired authorization: ask for another.
        var url = client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = options.S3BucketName,
            Key = Key(upload.ID),
            Verb = HttpVerb.PUT,
            Expires = expiresAt.UtcDateTime,
            ContentType = upload.ContentType
        });
        return Task.FromResult<CloudUploadAuthorizationResponse?>(new CloudUploadAuthorizationResponse(
            upload.ID,
            new Uri(url),
            "PUT",
            new Dictionary<string, string>
            {
                // Content-Type only, and it is not optional: it is part of the signature, so a
                // client that omits it or sends a different value is refused by S3.
                ["Content-Type"] = upload.ContentType
            },
            expiresAt));
    }

    public async Task<string?> CompleteDirectUpload(
        UploadRecord upload,
        CancellationToken cancellationToken)
    {
        var key = Key(upload.ID);
        var metadata = await Metadata(key, cancellationToken);
        if (metadata is null) return null;
        return metadata.ContentLength == upload.FileSize ? Reference(key) : null;
    }

    public async Task<MaterializedAudio> Materialize(
        UploadRecord upload,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(upload.FileName);
        if (string.IsNullOrWhiteSpace(extension) ||
            extension.Length > 10 ||
            extension.Any(character => !char.IsLetterOrDigit(character) && character != '.'))
        {
            extension = ".audio";
        }

        var path = Path.Combine(
            Path.GetTempPath(),
            $"audiochoice-{upload.ID}{extension.ToLowerInvariant()}");
        try
        {
            using (var response = await client.GetObjectAsync(
                new GetObjectRequest { BucketName = options.S3BucketName, Key = KeyFor(upload) },
                cancellationToken))
            {
                await response.WriteResponseStreamToFileAsync(path, false, cancellationToken);
            }

            var info = new FileInfo(path);
            if (info.Length != upload.FileSize)
            {
                throw new InvalidDataException("Temporary audio byte count is incorrect.");
            }
            await using var input = File.OpenRead(path);
            var hash = Convert.ToHexString(
                await SHA256.HashDataAsync(input, cancellationToken));
            if (!hash.Equals(upload.Fingerprint.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Temporary audio fingerprint is incorrect.");
            }
            return new MaterializedAudio(path, true);
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }

    public async Task Delete(UploadRecord upload, CancellationToken cancellationToken) =>
        await client.DeleteObjectAsync(
            new DeleteObjectRequest { BucketName = options.S3BucketName, Key = KeyFor(upload) },
            cancellationToken);

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

    private string Key(Guid uploadID) => $"{Prefix}/{uploadID}.audio";

    private string Reference(string key) => $"s3://{options.S3BucketName}/{key}";

    /// <summary>
    /// The key an upload's bytes actually live under.
    /// </summary>
    /// <remarks>
    /// Prefers the reference recorded when the upload was finalized, the same way the Azure
    /// implementation prefers its stored blob URI, so a worker can process an upload created by a
    /// deployment configured with a different bucket.
    ///
    /// Anything unrecognised falls back to the deterministic key rather than failing. That is what
    /// makes rows written by the Azure deployment -- whose <c>StoredPath</c> is an absolute
    /// <c>*.blob.core.windows.net</c> URL -- resolve to a sane S3 key instead of throwing after a
    /// provider switch. Temporary audio is retained for hours, so in practice almost nothing is
    /// in flight across a cutover anyway.
    /// </remarks>
    private string KeyFor(UploadRecord upload)
    {
        if (!string.IsNullOrWhiteSpace(upload.StoredPath) &&
            Uri.TryCreate(upload.StoredPath, UriKind.Absolute, out var stored) &&
            stored.Scheme == "s3" &&
            string.Equals(stored.Host, options.S3BucketName, StringComparison.OrdinalIgnoreCase))
        {
            return stored.AbsolutePath.TrimStart('/');
        }

        return Key(upload.ID);
    }

    public static IAmazonS3 CreateClient(TemporaryAudioStorageOptions options)
    {
        var configuration = new AmazonS3Config();
        if (!string.IsNullOrWhiteSpace(options.S3Region))
        {
            configuration.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(options.S3Region);
        }
        if (!string.IsNullOrWhiteSpace(options.S3ServiceURL))
        {
            // Overriding the endpoint clears the region the SDK would otherwise derive from it,
            // which is what MinIO and LocalStack need.
            configuration.ServiceURL = options.S3ServiceURL;
        }
        if (options.S3ForcePathStyle) configuration.ForcePathStyle = true;
        // No explicit credentials: the default chain resolves the ECS task role in deployment and a
        // developer's own profile locally, which is the direct counterpart of DefaultAzureCredential.
        return new AmazonS3Client(configuration);
    }
}

/// <summary>
/// Short-lived M4B relay storage for the desktop companion, held in S3.
/// </summary>
public sealed class S3CompanionTransferStorage(
    IAmazonS3 client,
    TemporaryAudioStorageOptions options) : ICompanionTransferStorage
{
    public bool IsAvailable => true;

    private string Prefix => string.IsNullOrWhiteSpace(options.CompanionTransferContainerName)
        ? "companion-transfers"
        : options.CompanionTransferContainerName;

    public Task<CompanionTransferUploadAuthorization?> CreateUploadAuthorization(
        CompanionTransferRecord transfer,
        CancellationToken cancellationToken)
    {
        var url = client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = options.S3BucketName,
            Key = Key(transfer.ID),
            Verb = HttpVerb.PUT,
            Expires = transfer.ExpiresAt.UtcDateTime,
            ContentType = transfer.ContentType
        });
        return Task.FromResult<CompanionTransferUploadAuthorization?>(new CompanionTransferUploadAuthorization(
            new Uri(url),
            "PUT",
            new Dictionary<string, string> { ["Content-Type"] = transfer.ContentType }));
    }

    public async Task<bool> VerifyUpload(CompanionTransferRecord transfer, CancellationToken cancellationToken)
    {
        var metadata = await Metadata(Key(transfer.ID), cancellationToken);
        return metadata is not null && metadata.ContentLength == transfer.FileSize;
    }

    public async Task<Uri?> CreateDownloadAuthorization(CompanionTransferRecord transfer, CancellationToken cancellationToken)
    {
        var key = Key(transfer.ID);
        if (await Metadata(key, cancellationToken) is null) return null;
        // Ten minutes, matching the Azure implementation: long enough for the handoff to start,
        // short enough that a leaked link is worth little.
        var url = client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = options.S3BucketName,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.AddMinutes(10)
        });
        return new Uri(url);
    }

    public async Task Delete(CompanionTransferRecord transfer, CancellationToken cancellationToken) =>
        await client.DeleteObjectAsync(
            new DeleteObjectRequest { BucketName = options.S3BucketName, Key = Key(transfer.ID) },
            cancellationToken);

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

    private string Key(Guid transferID) => $"{Prefix}/{transferID}.m4b";
}
