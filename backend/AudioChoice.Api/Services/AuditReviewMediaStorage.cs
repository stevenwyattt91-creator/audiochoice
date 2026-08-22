using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Azure.Storage.Blobs.Specialized;
using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

public sealed record StoredAuditReviewMedia(string ObjectName, long Length);

public interface IAuditReviewMediaStorage
{
    bool IsAvailable { get; }
    Task<StoredAuditReviewMedia> StoreSource(Guid assignmentID, string fileName, string contentType, Stream input, CancellationToken cancellationToken);
    Task<AuditReviewSourceUploadAuthorization?> CreateSourceUploadAuthorization(Guid assignmentID, string contentType, CancellationToken cancellationToken);
    Task<StoredAuditReviewMedia?> VerifySource(Guid assignmentID, CancellationToken cancellationToken);
    Task<MaterializedAudio> MaterializeSource(string objectName, CancellationToken cancellationToken);
    Task<StoredAuditReviewMedia> StoreClip(Guid assignmentID, Guid eventID, Stream input, CancellationToken cancellationToken);
    Task<Uri?> CreateReadAuthorization(string objectName, CancellationToken cancellationToken);
    Task DeletePrefix(Guid assignmentID, CancellationToken cancellationToken);
}

public sealed class UnavailableAuditReviewMediaStorage : IAuditReviewMediaStorage
{
    public bool IsAvailable => false;
    public Task<StoredAuditReviewMedia> StoreSource(Guid assignmentID, string fileName, string contentType, Stream input, CancellationToken cancellationToken) => throw new InvalidOperationException("Audit review media storage is unavailable.");
    public Task<AuditReviewSourceUploadAuthorization?> CreateSourceUploadAuthorization(Guid assignmentID, string contentType, CancellationToken cancellationToken) => Task.FromResult<AuditReviewSourceUploadAuthorization?>(null);
    public Task<StoredAuditReviewMedia?> VerifySource(Guid assignmentID, CancellationToken cancellationToken) => Task.FromResult<StoredAuditReviewMedia?>(null);
    public Task<MaterializedAudio> MaterializeSource(string objectName, CancellationToken cancellationToken) => throw new FileNotFoundException();
    public Task<StoredAuditReviewMedia> StoreClip(Guid assignmentID, Guid eventID, Stream input, CancellationToken cancellationToken) => throw new InvalidOperationException("Audit review media storage is unavailable.");
    public Task<Uri?> CreateReadAuthorization(string objectName, CancellationToken cancellationToken) => Task.FromResult<Uri?>(null);
    public Task DeletePrefix(Guid assignmentID, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class BlobAuditReviewMediaStorage(BlobServiceClient serviceClient, TemporaryAudioStorageOptions options) : IAuditReviewMediaStorage
{
    public bool IsAvailable => true;
    private string ContainerName => string.IsNullOrWhiteSpace(options.AuditReviewContainerName) ? "audit-review-media" : options.AuditReviewContainerName;
    private BlobContainerClient Container => serviceClient.GetBlobContainerClient(ContainerName);

    public async Task<StoredAuditReviewMedia> StoreSource(Guid assignmentID, string fileName, string contentType, Stream input, CancellationToken cancellationToken)
    {
        var objectName = $"{assignmentID:N}/source.m4b";
        var blob = Container.GetBlockBlobClient(objectName);
        await Container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
        await blob.UploadAsync(input, new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = contentType } }, cancellationToken);
        var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
        return new(objectName, properties.Value.ContentLength);
    }

    public async Task<AuditReviewSourceUploadAuthorization?> CreateSourceUploadAuthorization(Guid assignmentID, string contentType, CancellationToken cancellationToken)
    {
        await Container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
        var startsAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        var blob = Container.GetBlockBlobClient($"{assignmentID:N}/source.m4b");
        var delegation = await serviceClient.GetUserDelegationKeyAsync(startsAt, expiresAt, cancellationToken);
        var sas = new BlobSasBuilder { BlobContainerName = ContainerName, BlobName = blob.Name, Resource = "b", StartsOn = startsAt, ExpiresOn = expiresAt, Protocol = SasProtocol.Https };
        sas.SetPermissions(BlobSasPermissions.Create | BlobSasPermissions.Write);
        return new(new UriBuilder(blob.Uri) { Query = sas.ToSasQueryParameters(delegation.Value, options.StorageAccountName).ToString() }.Uri, "PUT", new Dictionary<string, string> { ["x-ms-blob-type"] = "BlockBlob", ["Content-Type"] = contentType }, expiresAt);
    }

    public async Task<StoredAuditReviewMedia?> VerifySource(Guid assignmentID, CancellationToken cancellationToken)
    {
        var objectName = $"{assignmentID:N}/source.m4b";
        var blob = Container.GetBlockBlobClient(objectName);
        if (!await blob.ExistsAsync(cancellationToken)) return null;
        var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
        return properties.Value.ContentLength > 0 ? new StoredAuditReviewMedia(objectName, properties.Value.ContentLength) : null;
    }

    public async Task<MaterializedAudio> MaterializeSource(string objectName, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"audiochoice-audit-{Guid.NewGuid():N}.m4b");
        try
        {
            await Container.GetBlockBlobClient(objectName).DownloadToAsync(path, cancellationToken);
            return new MaterializedAudio(path, true);
        }
        catch
        {
            if (File.Exists(path)) File.Delete(path);
            throw;
        }
    }

    public async Task<StoredAuditReviewMedia> StoreClip(Guid assignmentID, Guid eventID, Stream input, CancellationToken cancellationToken)
    {
        var objectName = $"{assignmentID:N}/clips/{eventID:N}.m4a";
        var blob = Container.GetBlockBlobClient(objectName);
        await Container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
        await blob.UploadAsync(input, new BlobUploadOptions { HttpHeaders = new BlobHttpHeaders { ContentType = "audio/mp4" } }, cancellationToken);
        var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
        return new(objectName, properties.Value.ContentLength);
    }

    public async Task<Uri?> CreateReadAuthorization(string objectName, CancellationToken cancellationToken)
    {
        var blob = Container.GetBlockBlobClient(objectName);
        if (!await blob.ExistsAsync(cancellationToken)) return null;
        var startsAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var delegation = await serviceClient.GetUserDelegationKeyAsync(startsAt, expiresAt, cancellationToken);
        var sas = new BlobSasBuilder { BlobContainerName = ContainerName, BlobName = objectName, Resource = "b", StartsOn = startsAt, ExpiresOn = expiresAt, Protocol = SasProtocol.Https };
        sas.SetPermissions(BlobSasPermissions.Read);
        return new UriBuilder(blob.Uri) { Query = sas.ToSasQueryParameters(delegation.Value, options.StorageAccountName).ToString() }.Uri;
    }

    public async Task DeletePrefix(Guid assignmentID, CancellationToken cancellationToken)
    {
        // A new environment may not have review media yet. Creating the private
        // container here makes queue resets safe before the first source upload.
        await Container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
        await foreach (var item in Container.GetBlobsAsync(BlobTraits.None, BlobStates.None, $"{assignmentID:N}/", cancellationToken))
        {
            await Container.DeleteBlobIfExistsAsync(item.Name, DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: cancellationToken);
        }
    }
}
