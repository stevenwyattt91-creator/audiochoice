using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using AudioChoice.Api.Contracts;
using AudioChoice.Api.Services;

namespace AudioChoice.Api.Processing;

/// <summary>
/// Private, fingerprint-keyed transcripts in S3, shared by the API and remote GPU workers.
/// The AWS counterpart to <c>BlobPrivateTranscriptStore</c>.
/// </summary>
/// <remarks>
/// Shared storage rather than a local disk for the same reason as on Azure: the GPU worker that
/// produces a transcript runs on a different host from the API that serves read-along, so a
/// file-backed store is invisible to whichever one did not write it.
///
/// The object name is derived exactly as the Azure implementation derives its blob name -- SHA-256
/// of <c>InMemoryScanCatalog.FingerprintKey</c>, lowercase hex, <c>.json</c> -- so transcripts
/// copied across from the blob container land on the keys this store will look for.
/// </remarks>
public sealed class S3PrivateTranscriptStore(
    IAmazonS3 client,
    TemporaryAudioStorageOptions options) : IPrivateTranscriptStore
{
    private string Prefix => string.IsNullOrWhiteSpace(options.TranscriptContainerName)
        ? "private-transcripts"
        : options.TranscriptContainerName;

    public async Task<PrivateTranscript?> Load(BookFingerprint fingerprint, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetObjectAsync(
                new GetObjectRequest { BucketName = options.S3BucketName, Key = Key(fingerprint) },
                cancellationToken);
            return await JsonSerializer.DeserializeAsync<PrivateTranscript>(
                response.ResponseStream,
                cancellationToken: cancellationToken);
        }
        catch (AmazonS3Exception failure) when (failure.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task Save(BookFingerprint fingerprint, PrivateTranscript transcript, CancellationToken cancellationToken)
    {
        await using var payload = new MemoryStream();
        await JsonSerializer.SerializeAsync(payload, transcript, cancellationToken: cancellationToken);
        payload.Position = 0;
        await client.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = options.S3BucketName,
                Key = Key(fingerprint),
                InputStream = payload,
                ContentType = "application/json"
            },
            cancellationToken);
    }

    private string Key(BookFingerprint fingerprint)
    {
        var fingerprintKey = InMemoryScanCatalog.FingerprintKey(fingerprint);
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintKey))).ToLowerInvariant();
        return $"{Prefix}/{name}.json";
    }
}
