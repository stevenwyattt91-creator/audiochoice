using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioChoice.Api.Processing;

/// <summary>
/// Talks only to AudioChoice's private faster-whisper service. It deliberately has
/// the same contract as the hosted transcription provider, so the scan pipeline and
/// its timestamp/filter behavior stay unchanged.
/// </summary>
public sealed class FasterWhisperTranscriptionProvider(
    HttpClient client,
    TimeSpan requestTimeout) : ITranscriptionProvider
{
    public string ModelName => "faster-whisper-large-v3-turbo";

    public async Task<IReadOnlyList<TranscriptSegment>> Transcribe(
        AudioChunk chunk,
        CancellationToken cancellationToken)
    {
        // Do not rely on HttpClient's platform default (100 seconds). A busy GPU
        // can legitimately need several minutes for a long or difficult chunk.
        // Keep a bounded, explicit timeout while still honoring scan cancellation.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(requestTimeout);
        var requestCancellation = timeout.Token;

        await using var audio = File.OpenRead(chunk.FilePath);
        using var content = new MultipartFormDataContent();
        using var audioContent = new StreamContent(audio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audioContent, "file", Path.GetFileName(chunk.FilePath));
        content.Add(new StringContent("en"), "language");
        content.Add(new StringContent("true"), "word_timestamps");

        using var response = await client.PostAsync("transcribe", content, requestCancellation);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(requestCancellation);
            throw new HttpRequestException(
                $"Local faster-whisper transcription failed with HTTP {(int)response.StatusCode}: {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(requestCancellation);
        var payload = await JsonSerializer.DeserializeAsync<WhisperResponse>(
            stream, cancellationToken: requestCancellation)
            ?? throw new InvalidOperationException("The faster-whisper service returned no transcript.");

        return payload.Segments
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .Select(item => new TranscriptSegment(item.Start, item.End, item.Text.Trim()))
            .ToArray();
    }

    private sealed record WhisperResponse(
        [property: JsonPropertyName("segments")] IReadOnlyList<WhisperSegment> Segments);

    private sealed record WhisperSegment(
        [property: JsonPropertyName("start")] double Start,
        [property: JsonPropertyName("end")] double End,
        [property: JsonPropertyName("text")] string Text);
}
