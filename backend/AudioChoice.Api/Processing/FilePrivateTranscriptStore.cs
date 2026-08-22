using System.Text.Json;
using AudioChoice.Api.Contracts;
using AudioChoice.Api.Services;

namespace AudioChoice.Api.Processing;

public sealed class FilePrivateTranscriptStore(
    AudioChoiceDataPaths dataPaths) : IPrivateTranscriptStore
{
    private readonly string _folder = dataPaths.Transcripts;

    public async Task<PrivateTranscript?> Load(
        BookFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        var path = PathFor(fingerprint);
        if (!File.Exists(path)) return null;

        await using var input = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PrivateTranscript>(
            input,
            cancellationToken: cancellationToken);
    }

    public async Task Save(
        BookFingerprint fingerprint,
        PrivateTranscript transcript,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_folder);

        var destination = PathFor(fingerprint);
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";

        await using (var output = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(
                output,
                transcript,
                cancellationToken: cancellationToken);
        }

        File.Move(temporary, destination, overwrite: true);
    }

    private string PathFor(BookFingerprint fingerprint)
    {
        var safeKey = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(
                    InMemoryScanCatalog.FingerprintKey(fingerprint))));
        return Path.Combine(_folder, $"{safeKey}.json");
    }
}
