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

                return payload.Segments
                    .Where(segment => !string.IsNullOrWhiteSpace(segment.Text))
                    .Select(segment => new TranscriptSegment(
                        segment.Start,
                        segment.End,
                        segment.Text.Trim()))
                    .ToArray();
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
        IReadOnlyList<VerboseSegment>? Segments);

    private sealed record VerboseSegment(
        [property: JsonPropertyName("start")] double Start,
        [property: JsonPropertyName("end")] double End,
        [property: JsonPropertyName("text")] string Text);
}
