using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioChoice.Api.Processing;

public sealed class OpenAITranscriptionProvider(
    HttpClient client,
    OpenAIProcessingOptions options,
    ILogger<OpenAITranscriptionProvider> logger) : ITranscriptionProvider
{
    public string ModelName => options.TranscriptionModel;

    public async Task<IReadOnlyList<TranscriptSegment>> Transcribe(
        AudioChunk chunk,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt += 1)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                "audio/transcriptions");

            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                options.ApiKey);

            await using var audio = File.OpenRead(chunk.FilePath);
            using var content = new MultipartFormDataContent();
            using var audioContent = new StreamContent(audio);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

            content.Add(audioContent, "file", Path.GetFileName(chunk.FilePath));
            content.Add(new StringContent(options.TranscriptionModel), "model");
            content.Add(new StringContent("verbose_json"), "response_format");
            content.Add(new StringContent("segment"), "timestamp_granularities[]");
            content.Add(new StringContent("word"), "timestamp_granularities[]");
            request.Content = content;

            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                await using var responseStream = await response.Content
                    .ReadAsStreamAsync(cancellationToken);

                var payload = await JsonSerializer.DeserializeAsync<VerboseTranscript>(
                    responseStream,
                    cancellationToken: cancellationToken);

                if (payload?.Segments is null)
                {
                    throw new InvalidOperationException(
                        "The transcription response did not contain timestamped segments.");
                }

                // Unlike faster-whisper, which nests each segment's own words inside it, this
                // API returns one flat words array for the whole chunk. Each word is assigned
                // to whichever segment's time range contains its midpoint, so a word that
                // straddles a segment boundary lands on the segment its narration mostly
                // belongs to rather than always the earlier or later one.
                var words = payload.Words ?? [];
                var wordIndex = 0;

                var orderedSegments = payload.Segments
                    .Where(segment => !string.IsNullOrWhiteSpace(segment.Text))
                    .ToArray();
                var result = new TranscriptSegment[orderedSegments.Length];

                for (var segmentIndex = 0; segmentIndex < orderedSegments.Length; segmentIndex++)
                {
                    var segment = orderedSegments[segmentIndex];
                    var isLastSegment = segmentIndex == orderedSegments.Length - 1;
                    var segmentWords = new List<TranscriptWord>();

                    while (wordIndex < words.Count)
                    {
                        var word = words[wordIndex];
                        var midpoint = (word.Start + word.End) / 2;
                        // The last segment claims every remaining word, so a word timed
                        // slightly past the transcript's own final segment boundary (rounding
                        // between the two granularities in the same response) is not silently
                        // dropped instead of attached anywhere.
                        if (!isLastSegment && midpoint > segment.End) break;

                        var trimmed = word.Word.Trim();
                        if (trimmed.Length > 0)
                        {
                            segmentWords.Add(new TranscriptWord(trimmed, word.Start, word.End));
                        }
                        wordIndex += 1;
                    }

                    result[segmentIndex] = new TranscriptSegment(
                        segment.Start,
                        segment.End,
                        segment.Text.Trim(),
                        segmentWords.Count > 0 ? segmentWords : null);
                }

                return result;
            }

            if (!ShouldRetry(response.StatusCode, attempt))
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException(
                    $"Transcription failed with HTTP {(int)response.StatusCode}: {error}");
            }

            var delay = RetryDelay(response, attempt);
            logger.LogWarning(
                "Transcription request retry {Attempt} after {Delay}.",
                attempt + 1,
                delay);

            await Task.Delay(delay, cancellationToken);
        }
    }

    private bool ShouldRetry(HttpStatusCode statusCode, int attempt) =>
        attempt < options.MaximumRetries &&
        (statusCode == HttpStatusCode.TooManyRequests ||
         (int)statusCode >= 500);

    private static TimeSpan RetryDelay(
        HttpResponseMessage response,
        int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is TimeSpan retryAfter)
        {
            return retryAfter;
        }

        return TimeSpan.FromSeconds(Math.Pow(2, attempt));
    }

    private sealed record VerboseTranscript(
        [property: JsonPropertyName("segments")]
        IReadOnlyList<VerboseSegment>? Segments,
        // Present only when "word" was requested alongside "segment" in
        // timestamp_granularities[]. One flat list for the whole chunk, not nested inside
        // each segment the way faster-whisper's own response shape is.
        [property: JsonPropertyName("words")]
        IReadOnlyList<VerboseWord>? Words = null);

    private sealed record VerboseSegment(
        [property: JsonPropertyName("start")] double Start,
        [property: JsonPropertyName("end")] double End,
        [property: JsonPropertyName("text")] string Text);

    private sealed record VerboseWord(
        [property: JsonPropertyName("word")] string Word,
        [property: JsonPropertyName("start")] double Start,
        [property: JsonPropertyName("end")] double End);
}
