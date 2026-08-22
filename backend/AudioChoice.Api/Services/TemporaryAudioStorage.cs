using AudioChoice.Api.Contracts;

#if POSTGRES
using System.Security.Cryptography;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
#endif

namespace AudioChoice.Api.Services;

public sealed record MaterializedAudio(string Path, bool DeleteWhenDisposed) : IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        if (DeleteWhenDisposed && File.Exists(Path)) File.Delete(Path);
        return ValueTask.CompletedTask;
    }
}

public interface ITemporaryAudioStorage
{
    bool UsesDirectUpload { get; }
    Task<CloudUploadAuthorizationResponse?> CreateDirectUploadAuthorization(
        UploadRecord upload,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken);
    Task<string?> CompleteDirectUpload(
        UploadRecord upload,
        CancellationToken cancellationToken);
    Task<MaterializedAudio> Materialize(
        UploadRecord upload,
        CancellationToken cancellationToken);
    Task Delete(UploadRecord upload, CancellationToken cancellationToken);
}

/// <summary>
/// Short-lived M4B relay storage for the desktop companion. It is separate from
/// scanning: nothing handed through this interface is submitted to a scanner.
/// </summary>
public interface ICompanionTransferStorage
{
    bool IsAvailable { get; }
    Task<CompanionTransferUploadAuthorization?> CreateUploadAuthorization(
        CompanionTransferRecord transfer,
        CancellationToken cancellationToken);
    Task<bool> VerifyUpload(CompanionTransferRecord transfer, CancellationToken cancellationToken);
    Task<Uri?> CreateDownloadAuthorization(CompanionTransferRecord transfer, CancellationToken cancellationToken);
    Task Delete(CompanionTransferRecord transfer, CancellationToken cancellationToken);
}

public sealed record CompanionTransferUploadAuthorization(Uri UploadURL, string Method, IReadOnlyDictionary<string, string> Headers);

public sealed class UnavailableCompanionTransferStorage : ICompanionTransferStorage
{
    public bool IsAvailable => false;
    public Task<CompanionTransferUploadAuthorization?> CreateUploadAuthorization(CompanionTransferRecord transfer, CancellationToken cancellationToken) => Task.FromResult<CompanionTransferUploadAuthorization?>(null);
    public Task<bool> VerifyUpload(CompanionTransferRecord transfer, CancellationToken cancellationToken) => Task.FromResult(false);
    public Task<Uri?> CreateDownloadAuthorization(CompanionTransferRecord transfer, CancellationToken cancellationToken) => Task.FromResult<Uri?>(null);
    public Task Delete(CompanionTransferRecord transfer, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class LocalTemporaryAudioStorage : ITemporaryAudioStorage
{
    public bool UsesDirectUpload => false;
    public Task<CloudUploadAuthorizationResponse?> CreateDirectUploadAuthorization(
        UploadRecord upload, DateTimeOffset expiresAt, CancellationToken cancellationToken) =>
        Task.FromResult<CloudUploadAuthorizationResponse?>(null);
    public Task<string?> CompleteDirectUpload(
        UploadRecord upload, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
    public Task<MaterializedAudio> Materialize(
        UploadRecord upload, CancellationToken cancellationToken) =>
        Task.FromResult(new MaterializedAudio(
            upload.StoredPath ?? throw new FileNotFoundException("Temporary audio is unavailable."),
            false));
    public Task Delete(UploadRecord upload, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(upload.StoredPath) && File.Exists(upload.StoredPath))
        {
            File.Delete(upload.StoredPath);
        }
        return Task.CompletedTask;
    }
}

#if POSTGRES
public sealed class BlobTemporaryAudioStorage(
    BlobServiceClient serviceClient,
    TemporaryAudioStorageOptions options) : ITemporaryAudioStorage
{
    public bool UsesDirectUpload => true;

    public async Task<CloudUploadAuthorizationResponse?> CreateDirectUploadAuthorization(
        UploadRecord upload,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        var startsAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var delegationKey = await serviceClient.GetUserDelegationKeyAsync(
            startsAt,
            expiresAt,
            cancellationToken);
        var blob = Blob(upload.ID);
        var sas = new BlobSasBuilder
        {
            BlobContainerName = options.ContainerName,
            BlobName = blob.Name,
            Resource = "b",
            StartsOn = startsAt,
            ExpiresOn = expiresAt,
            Protocol = SasProtocol.Https
        };
        sas.SetPermissions(BlobSasPermissions.Create | BlobSasPermissions.Write);
        var query = sas.ToSasQueryParameters(
            delegationKey.Value,
            options.StorageAccountName);
        return new CloudUploadAuthorizationResponse(
            upload.ID,
            new UriBuilder(blob.Uri) { Query = query.ToString() }.Uri,
            "PUT",
            new Dictionary<string, string>
            {
                ["x-ms-blob-type"] = "BlockBlob",
                ["Content-Type"] = upload.ContentType
            },
            expiresAt);
    }

    public async Task<string?> CompleteDirectUpload(
        UploadRecord upload,
        CancellationToken cancellationToken)
    {
        var blob = Blob(upload.ID);
        if (!await blob.ExistsAsync(cancellationToken)) return null;
        var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
        return properties.Value.ContentLength == upload.FileSize
            ? blob.Uri.ToString()
            : null;
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
            // Direct uploads are recorded with their exact Blob URI when the API
            // finalizes them. Prefer that durable reference over reconstructing a
            // name from the upload ID: it lets a worker safely process uploads
            // created by another deployment with a different storage setting.
            await Blob(upload).DownloadToAsync(path, cancellationToken);
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
        await Blob(upload).DeleteIfExistsAsync(cancellationToken: cancellationToken);

    private BlockBlobClient Blob(UploadRecord upload)
    {
        if (Uri.TryCreate(upload.StoredPath, UriKind.Absolute, out var storedUri) &&
            storedUri.Scheme == Uri.UriSchemeHttps &&
            string.Equals(
                storedUri.Host,
                $"{options.StorageAccountName}.blob.core.windows.net",
                StringComparison.OrdinalIgnoreCase))
        {
            return new BlockBlobClient(storedUri, new DefaultAzureCredential());
        }

        return Blob(upload.ID);
    }

    private Azure.Storage.Blobs.Specialized.BlockBlobClient Blob(Guid uploadID) =>
        serviceClient.GetBlobContainerClient(options.ContainerName)
            .GetBlockBlobClient($"{uploadID}.audio");

    public static BlobServiceClient CreateClient(TemporaryAudioStorageOptions options) =>
        new(
            new Uri($"https://{options.StorageAccountName}.blob.core.windows.net"),
            new DefaultAzureCredential());
}

public sealed class BlobCompanionTransferStorage(
    BlobServiceClient serviceClient,
    TemporaryAudioStorageOptions options) : ICompanionTransferStorage
{
    public bool IsAvailable => true;

    public async Task<CompanionTransferUploadAuthorization?> CreateUploadAuthorization(
        CompanionTransferRecord transfer,
        CancellationToken cancellationToken)
    {
        var startsAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var delegationKey = await serviceClient.GetUserDelegationKeyAsync(startsAt, transfer.ExpiresAt, cancellationToken);
        var blob = Blob(transfer.ID);
        var sas = new BlobSasBuilder { BlobContainerName = Container, BlobName = blob.Name, Resource = "b", StartsOn = startsAt, ExpiresOn = transfer.ExpiresAt, Protocol = SasProtocol.Https };
        sas.SetPermissions(BlobSasPermissions.Create | BlobSasPermissions.Write);
        var query = sas.ToSasQueryParameters(delegationKey.Value, options.StorageAccountName);
        return new CompanionTransferUploadAuthorization(
            new UriBuilder(blob.Uri) { Query = query.ToString() }.Uri,
            "PUT",
            new Dictionary<string, string> { ["x-ms-blob-type"] = "BlockBlob", ["Content-Type"] = transfer.ContentType });
    }

    public async Task<bool> VerifyUpload(CompanionTransferRecord transfer, CancellationToken cancellationToken)
    {
        var blob = Blob(transfer.ID);
        if (!await blob.ExistsAsync(cancellationToken)) return false;
        var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
        return properties.Value.ContentLength == transfer.FileSize;
    }

    public async Task<Uri?> CreateDownloadAuthorization(CompanionTransferRecord transfer, CancellationToken cancellationToken)
    {
        var blob = Blob(transfer.ID);
        if (!await blob.ExistsAsync(cancellationToken)) return null;
        var startsAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        var delegationKey = await serviceClient.GetUserDelegationKeyAsync(startsAt, expiresAt, cancellationToken);
        var sas = new BlobSasBuilder { BlobContainerName = Container, BlobName = blob.Name, Resource = "b", StartsOn = startsAt, ExpiresOn = expiresAt, Protocol = SasProtocol.Https };
        sas.SetPermissions(BlobSasPermissions.Read);
        var query = sas.ToSasQueryParameters(delegationKey.Value, options.StorageAccountName);
        return new UriBuilder(blob.Uri) { Query = query.ToString() }.Uri;
    }

    public async Task Delete(CompanionTransferRecord transfer, CancellationToken cancellationToken) =>
        await Blob(transfer.ID).DeleteIfExistsAsync(cancellationToken: cancellationToken);

    private string Container => string.IsNullOrWhiteSpace(options.CompanionTransferContainerName) ? "companion-transfers" : options.CompanionTransferContainerName;
    private BlockBlobClient Blob(Guid transferID) => serviceClient.GetBlobContainerClient(Container).GetBlockBlobClient($"{transferID}.m4b");
}
#endif
