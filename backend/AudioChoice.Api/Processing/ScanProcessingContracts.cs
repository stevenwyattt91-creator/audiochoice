using AudioChoice.Api.Contracts;
using AudioChoice.Api.Services;

namespace AudioChoice.Api.Processing;

public sealed record AudioChunk(
    string FilePath,
    double StartTime,
    double EndTime,
    Action? Cleanup = null)
{
    public void DisposeFile() => Cleanup?.Invoke();
}

public sealed record TranscriptSegment(
    double StartTime,
    double EndTime,
    string Text);

public sealed record PrivateTranscript(
    string Version,
    string Language,
    string TranscriptionModel,
    DateTimeOffset CreatedAt,
    IReadOnlyList<TranscriptSegment> Segments,
    bool? IsComplete = null,
    IReadOnlyList<TranscriptionChunkCheckpoint>? Checkpoints = null);

public sealed record TranscriptionChunkCheckpoint(
    Guid JobID,
    int ChunkIndex,
    int TotalChunks,
    double StartTime,
    double EndTime,
    IReadOnlyList<TranscriptSegment> Segments,
    string Status,
    int RetryCount,
    string ModelName);

public interface IAudioChunker
{
    IAsyncEnumerable<AudioChunk> CreateChunks(
        string audioFilePath,
        CancellationToken cancellationToken);
}

public interface IPreMaterializedAudioChunker
{
    Task<IReadOnlyList<AudioChunk>> Materialize(
        string audioFilePath,
        CancellationToken cancellationToken);
}

public interface ITranscriptionProvider
{
    string ModelName { get; }

    Task<IReadOnlyList<TranscriptSegment>> Transcribe(
        AudioChunk chunk,
        CancellationToken cancellationToken);
}

public interface IContentAnalysisProvider
{
    string ScannerVersion { get; }

    Task<IReadOnlyList<ScanEvent>> Analyze(
        IReadOnlyList<TranscriptSegment> segments,
        Action<double>? reportProgress,
        CancellationToken cancellationToken);
}

public interface IPrivateTranscriptStore
{
    Task<PrivateTranscript?> Load(
        BookFingerprint fingerprint,
        CancellationToken cancellationToken) =>
        Task.FromResult<PrivateTranscript?>(null);

    Task Save(
        BookFingerprint fingerprint,
        PrivateTranscript transcript,
        CancellationToken cancellationToken);
}

public interface IScanPipeline
{
    Task<ScanResult> Process(
        UploadRecord upload,
        Action<int, string>? reportProgress,
        CancellationToken cancellationToken,
        Action<int, int>? reportChunkProgress = null,
        Guid? scanID = null);
}
