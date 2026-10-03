using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using AudioChoice.Api.Contracts;
using AudioChoice.Api.Services;

namespace AudioChoice.Api.Processing;

/// <summary>
/// Private, fingerprint-keyed transcripts in S3, shared by the API and the GPU worker.
/// </summary>
/// <remarks>
/// The third implementation of this interface, added for the same reason the Blob one was: a
/// transcript has to outlive the host that produced it. The GPU instances are replaced nightly, so
/// a file-backed transcript on a container's own disk is destroyed within a day -- while the scan
/// result survives in the catalog, which is exactly the combination that leaves a book showing
/// working filters and no read-along timing data.
///
/// Why this exists when Blob already did: a transcript cannot be regenerated. Uploaded audio is
/// deleted the moment a scan completes, so losing one means re-uploading and re-transcribing the
/// book at full cost. That makes transcript storage the one piece that has to keep working after
/// everything else moves, and it was the last thing tying the scanner to Azure.
///
/// Mirrors <c>BlobPrivateTranscriptStore</c> deliberately -- same key derivation, same payload --
/// so a transcript written by either is readable by the other during a move, and neither becomes
/// the only way to reach a book already scanned. Not behind <c>#if POSTGRES</c>, unlike the Blob
/// store: nothing here needs a database, and a file-backed single-user deployment is the case this
/// was written for.
///
/// The mobile apps never receive a URI or a transcript payload from this store. The bucket blocks
/// public access and the app has no credential for it; a device gets timings and offsets only, via
/// /v1/reader/alignments.
/// </remarks>
public sealed class S3PrivateTranscriptStore(
    IAmazonS3 s3,
    TemporaryAudioStorageOptions options) : IPrivateTranscriptStore
{
    public async Task<PrivateTranscript?> Load(
        BookFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await s3.GetObjectAsync(
                new GetObjectRequest
                {
                    BucketName = options.TranscriptBucketName,
                    Key = Key(fingerprint)
                },
                cancellationToken);
            return await JsonSerializer.DeserializeAsync<PrivateTranscript>(
                response.ResponseStream,
                cancellationToken: cancellationToken);
        }
        // A book nobody has scanned yet is the ordinary case, not a failure, and it has to read as
        // null rather than throwing -- every caller treats null as "transcribe it" and an exception
        // here would fail the job instead.
        catch (AmazonS3Exception error) when (error.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task Save(
        BookFingerprint fingerprint,
        PrivateTranscript transcript,
        CancellationToken cancellationToken)
    {
        // Serialized fully before the request rather than streamed into it. The pipeline saves a
        // partial transcript after every completed chunk, so this runs many times per book, and a
        // half-written object would be indistinguishable from a complete one on the next Load --
        // which the reuse shortcut would then trust forever.
        await using var payload = new MemoryStream();
        await JsonSerializer.SerializeAsync(payload, transcript, cancellationToken: cancellationToken);
        payload.Position = 0;
        await s3.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = options.TranscriptBucketName,
                Key = Key(fingerprint),
                ContentType = "application/json",
                InputStream = payload
            },
            cancellationToken);
    }

    /// <summary>
    /// The object key for an edition, derived exactly as the Blob store derives its blob name.
    /// </summary>
    /// <remarks>
    /// Hashed rather than used directly because a fingerprint key is not a safe object name, and
    /// because the key would otherwise carry the file's own hash and size into a bucket listing.
    /// Identical to the Blob store's derivation on purpose: the same edition resolves to the same
    /// name in both, so one can be copied to the other with no re-keying.
    /// </remarks>
    private static string Key(BookFingerprint fingerprint)
    {
        var fingerprintKey = InMemoryScanCatalog.FingerprintKey(fingerprint);
        var name = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintKey))).ToLowerInvariant();
        return $"{name}.json";
    }
}
