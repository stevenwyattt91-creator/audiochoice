using System.Diagnostics;

namespace AudioChoice.Api.Processing;

public sealed record TranscribedChunk(
    int Index,
    int Total,
    AudioChunk Chunk,
    IReadOnlyList<TranscriptSegment> Segments,
    int RetryCount,
    string ModelName);

/// <summary>Bounded, cancellable chunk execution with deterministic ordering.</summary>
public sealed class ConcurrentChunkTranscriber(
    ITranscriptionProvider provider,
    OpenAIProcessingOptions options,
    ILogger<ConcurrentChunkTranscriber> logger)
{
    public async Task<IReadOnlyList<TranscribedChunk>> Transcribe(
        IReadOnlyList<AudioChunk> chunks,
        Action<int, int>? progress,
        CancellationToken cancellationToken)
    {
        var parallelism = Math.Max(1, options.TranscriptionWorkers) *
                          Math.Max(1, options.TranscriptionConcurrencyPerWorker);
        using var gate = new SemaphoreSlim(parallelism, parallelism);
        var completed = 0;
        var tasks = chunks.Select((chunk, index) => Run(index, chunk, chunks.Count, gate,
            () => progress?.Invoke(Interlocked.Increment(ref completed), chunks.Count), cancellationToken));
        return (await Task.WhenAll(tasks)).OrderBy(item => item.Index).ToArray();
    }

    private async Task<TranscribedChunk> Run(
        int index, AudioChunk chunk, int total, SemaphoreSlim gate,
        Action completed, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            for (var retry = 0; ; retry++)
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    var segments = await provider.Transcribe(chunk, cancellationToken);
                    logger.LogInformation("Transcription chunk {Index} / {Total} completed; retries {Retries}; elapsedMs {ElapsedMs}.",
                        index + 1, total, retry, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    // Progress persistence is observability, not transcription. Never turn a
                    // successful Whisper result into an expensive retranscription if its save fails.
                    try { completed(); }
                    catch (Exception error) { logger.LogWarning(error, "Could not persist progress for chunk {Index} / {Total}.", index + 1, total); }
                    return new TranscribedChunk(index, total, chunk, segments, retry, provider.ModelName);
                }
                catch (Exception) when (retry < Math.Max(0, options.TranscriptionMaximumRetries))
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, retry));
                    logger.LogWarning("Retrying transcription chunk {Index} / {Total}; retry {Retry}; delay {Delay}.", index + 1, total, retry + 1, delay);
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }
        finally { gate.Release(); }
    }
}
