using AudioChoice.Api.Contracts;
using AudioChoice.Api.Services;

namespace AudioChoice.Api.Processing;

public sealed class ScanPipeline(
    IAudioChunker chunker,
    ITranscriptionProvider transcriptionProvider,
    IContentAnalysisProvider analysisProvider,
    IPrivateTranscriptStore transcriptStore,
    OpenAIProcessingOptions options,
    ConcurrentChunkTranscriber? concurrentTranscriber = null) : IScanPipeline
{
    public async Task<ScanResult> Process(
        UploadRecord upload,
        Action<int, string>? reportProgress,
        CancellationToken cancellationToken,
        Action<int, int>? reportChunkProgress = null,
        Guid? scanID = null)
    {
        var existingTranscript = await transcriptStore.Load(
            upload.Fingerprint,
            cancellationToken);
        IReadOnlyList<TranscriptSegment> normalizedSegments;

        if (existingTranscript is not null && existingTranscript.IsComplete is not false &&
            existingTranscript.Segments.Count > 0)
        {
            normalizedSegments = existingTranscript.Segments;
            reportProgress?.Invoke(75, "transcription_complete");
        }
        else
        {
            if (!upload.IsUploaded || string.IsNullOrWhiteSpace(upload.StoredPath))
            {
                throw new InvalidOperationException(
                    "The scan job has neither a saved transcript nor a completed private audio upload.");
            }

            if (chunker is IPreMaterializedAudioChunker preChunker)
            {
                normalizedSegments = await ProcessMaterialized(
                    preChunker, upload, existingTranscript, reportChunkProgress, cancellationToken, scanID);
            }
            else
            {
            var transcriptSegments = existingTranscript?.Segments.ToList() ?? [];
            var completedThrough = transcriptSegments.Count == 0
                ? 0
                : transcriptSegments.Max(segment => segment.EndTime);
            var chunkCount = 0;
            var completedChunks = 0;

            await foreach (var chunk in chunker.CreateChunks(
                upload.StoredPath,
                cancellationToken))
            {
                if (chunk.EndTime <= completedThrough + 0.001)
                {
                    continue;
                }
                if (chunk.EndTime > options.MaximumAudioDurationSeconds)
                {
                    throw new InvalidOperationException(
                        $"The audiobook chunk ended at {chunk.EndTime:F3} seconds, " +
                        $"above the configured paid-processing limit of " +
                        $"{options.MaximumAudioDurationSeconds:F3} seconds.");
                }

                chunkCount += 1;
                if (chunkCount > options.MaximumChunksPerJob)
                {
                    throw new InvalidOperationException(
                        "The audiobook exceeded the configured chunk limit.");
                }

                var chunkSegments = await transcriptionProvider.Transcribe(
                    chunk,
                    cancellationToken);

                transcriptSegments.AddRange(chunkSegments.Select(segment =>
                    segment with
                    {
                        StartTime = segment.StartTime + chunk.StartTime,
                        EndTime = segment.EndTime + chunk.StartTime
                    }));

                var duration = Math.Max(1, upload.Fingerprint.Duration ?? chunk.EndTime);
                var transcribed = Math.Clamp(chunk.EndTime / duration, 0, 1);
                reportProgress?.Invoke(10 + (int)Math.Round(transcribed * 65), "transcribing");
                completedChunks += 1;
                reportChunkProgress?.Invoke(completedChunks, chunkCount);

                if (transcriptSegments.Count >
                    options.MaximumTranscriptSegmentsPerJob)
                {
                    throw new InvalidOperationException(
                        "The audiobook exceeded the configured transcript segment limit.");
                }

                var partialSegments = NormalizeSegments(transcriptSegments);
                await transcriptStore.Save(
                    upload.Fingerprint,
                    new PrivateTranscript(
                        "1.0", "en", transcriptionProvider.ModelName,
                        existingTranscript?.CreatedAt ?? DateTimeOffset.UtcNow,
                        partialSegments, false),
                    cancellationToken);
                chunk.DisposeFile();
            }

            normalizedSegments = NormalizeSegments(transcriptSegments);

            var transcript = new PrivateTranscript(
                "1.0",
                "en",
                transcriptionProvider.ModelName,
                DateTimeOffset.UtcNow,
                normalizedSegments,
                true);

            await transcriptStore.Save(
                upload.Fingerprint,
                transcript,
                cancellationToken);
            }
        }

        reportProgress?.Invoke(78, "analyzing");
        var events = await analysisProvider.Analyze(
            normalizedSegments,
            progress => reportProgress?.Invoke(78 + (int)Math.Round(progress * 18), "analyzing"),
            cancellationToken);
        reportProgress?.Invoke(97, "finalizing");

        return new ScanResult(
            events,
            DateTimeOffset.UtcNow,
            analysisProvider.ScannerVersion);
    }

    private async Task<IReadOnlyList<TranscriptSegment>> ProcessMaterialized(
        IPreMaterializedAudioChunker preChunker,
        UploadRecord upload,
        PrivateTranscript? existingTranscript,
        Action<int, int>? reportChunkProgress,
        CancellationToken cancellationToken,
        Guid? scanID)
    {
        var chunks = await preChunker.Materialize(upload.StoredPath!, cancellationToken);
        // Chunks finish out of order. Never use the maximum transcript timestamp as the
        // resume point, because that could skip a failed chunk that sits before a later one.
        // Checkpoints are matched to materialized chunk boundaries and only the contiguous
        // prefix is considered complete.
        var completedThrough = 0d;
        var completedEnds = existingTranscript?.Checkpoints?
            .Where(checkpoint => string.Equals(checkpoint.Status, "completed", StringComparison.OrdinalIgnoreCase))
            .Select(checkpoint => checkpoint.EndTime)
            .ToArray() ?? [];
        if (completedEnds.Length > 0)
        {
            foreach (var chunk in chunks.OrderBy(item => item.StartTime))
            {
                if (!completedEnds.Any(end => Math.Abs(end - chunk.EndTime) <= 0.01)) break;
                completedThrough = chunk.EndTime;
            }
        }
        else if (existingTranscript?.Segments.Count > 0)
        {
            completedThrough = existingTranscript.Segments.Max(segment => segment.EndTime);
        }
        var pending = chunks.Where(chunk =>
            chunk.EndTime > completedThrough + 0.001 &&
            chunk.EndTime <= options.MaximumAudioDurationSeconds).ToArray();
        if (pending.Length != chunks.Count)
            foreach (var skipped in chunks.Where(chunk => !pending.Contains(chunk))) skipped.DisposeFile();
        if (concurrentTranscriber is null)
            throw new InvalidOperationException("Concurrent transcription is not configured.");
        var segments = existingTranscript?.Segments.ToList() ?? [];
        var checkpoints = existingTranscript?.Checkpoints?.ToList() ?? [];
        var checkpointGate = new SemaphoreSlim(1, 1);
        async Task CheckpointCompletedChunk(TranscribedChunk result)
        {
            await checkpointGate.WaitAsync(cancellationToken);
            try
            {
                segments.AddRange(result.Segments.Select(segment => segment with
                {
                    StartTime = segment.StartTime + result.Chunk.StartTime,
                    EndTime = segment.EndTime + result.Chunk.StartTime
                }));
                checkpoints.Add(new TranscriptionChunkCheckpoint(
                    scanID ?? Guid.Empty, result.Index, result.Total,
                    result.Chunk.StartTime, result.Chunk.EndTime,
                    result.Segments, "completed", result.RetryCount, result.ModelName));
                await transcriptStore.Save(upload.Fingerprint,
                    new PrivateTranscript("1.0", "en", result.ModelName,
                        existingTranscript?.CreatedAt ?? DateTimeOffset.UtcNow,
                        NormalizeSegments(segments), false, checkpoints.ToArray()), cancellationToken);
            }
            finally { checkpointGate.Release(); }
        }

        IReadOnlyList<TranscribedChunk> results;
        try
        {
            results = await concurrentTranscriber.Transcribe(
                pending, reportChunkProgress, cancellationToken, CheckpointCompletedChunk);
        }
        finally { checkpointGate.Dispose(); }

        foreach (var result in results)
        {
            result.Chunk.DisposeFile();
        }
        var normalized = NormalizeSegments(segments);
        await transcriptStore.Save(upload.Fingerprint,
            new PrivateTranscript("1.0", "en", transcriptionProvider.ModelName,
                existingTranscript?.CreatedAt ?? DateTimeOffset.UtcNow,
                normalized, true, checkpoints.ToArray()), cancellationToken);
        return normalized;
    }

    private static IReadOnlyList<TranscriptSegment> NormalizeSegments(
        IEnumerable<TranscriptSegment> segments)
    {
        var normalized = new List<TranscriptSegment>();

        foreach (var segment in segments.OrderBy(item => item.StartTime))
        {
            var duplicate = normalized.Any(existing =>
                string.Equals(
                    existing.Text,
                    segment.Text,
                    StringComparison.OrdinalIgnoreCase) &&
                existing.StartTime < segment.EndTime &&
                segment.StartTime < existing.EndTime);

            if (!duplicate)
            {
                normalized.Add(segment);
            }
        }

        return normalized;
    }
}
