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
    HttpClient client) : ITranscriptionProvider
{
    public string ModelName => "faster-whisper-large-v3-turbo";

    public async Task<IReadOnlyList<TranscriptSegment>> Transcribe(
        AudioChunk chunk,
        CancellationToken cancellationToken)
    {
        await using var audio = File.OpenRead(chunk.FilePath);
        using var content = new MultipartFormDataContent();
        using var audioContent = new StreamContent(audio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(audioContent, "file", Path.GetFileName(chunk.FilePath));
        content.Add(new StringContent("en"), "language");
        content.Add(new StringContent("true"), "word_timestamps");

        using var response = await client.PostAsync("transcribe", content, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Local faster-whisper transcription failed with HTTP {(int)response.StatusCode}: {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<WhisperResponse>(
            stream, cancellationToken: cancellationToken)
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
