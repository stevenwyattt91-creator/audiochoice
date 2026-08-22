#if POSTGRES
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AudioChoice.Api.Contracts;
using AudioChoice.Api.Services;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace AudioChoice.Api.Processing;

/// <summary>
/// Private, fingerprint-keyed transcripts shared by the API and remote GPU workers.
/// The mobile apps never receive a URI or transcript payload from this store.
/// </summary>
public sealed class BlobPrivateTranscriptStore(
    BlobServiceClient serviceClient,
    TemporaryAudioStorageOptions options) : IPrivateTranscriptStore
{
    public async Task<PrivateTranscript?> Load(BookFingerprint fingerprint, CancellationToken cancellationToken)
    {
        var blob = Blob(fingerprint);
        if (!await blob.ExistsAsync(cancellationToken)) return null;
        var response = await blob.DownloadStreamingAsync(cancellationToken: cancellationToken);
        return await JsonSerializer.DeserializeAsync<PrivateTranscript>(
            response.Value.Content,
            cancellationToken: cancellationToken);
    }

    public async Task Save(BookFingerprint fingerprint, PrivateTranscript transcript, CancellationToken cancellationToken)
    {
        await serviceClient.GetBlobContainerClient(options.TranscriptContainerName)
            .CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: cancellationToken);
        var blob = Blob(fingerprint);
        await using var payload = new MemoryStream();
        await JsonSerializer.SerializeAsync(payload, transcript, cancellationToken: cancellationToken);
        payload.Position = 0;
        await blob.UploadAsync(payload, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" }
        }, cancellationToken);
    }

    private BlobClient Blob(BookFingerprint fingerprint)
    {
        var fingerprintKey = InMemoryScanCatalog.FingerprintKey(fingerprint);
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintKey))).ToLowerInvariant();
        return serviceClient.GetBlobContainerClient(options.TranscriptContainerName)
            .GetBlobClient($"{name}.json");
    }
}
#endif
