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

/// <summary>
/// Classifies passages whose <see cref="TranscriptSegment.StartTime"/> and
/// <see cref="TranscriptSegment.EndTime"/> are character offsets into a book's text rather
/// than seconds into its audio.
/// </summary>
/// <remarks>
/// A separate method rather than a flag on <see cref="IContentAnalysisProvider.Analyze"/>,
/// because the two paths cannot share their post-processing. <c>Analyze</c> finishes by
/// applying <c>SceneEventPostProcessor</c>, whose 45-second merge gap, 8-second padding and
/// 30-second minimum scene length are not merely unhelpful as character counts but wrong:
/// a 30-character minimum would stretch or discard real passages, and the ±30 clamps in
/// scene verification would move boundaries by thirty characters. Everything that is
/// genuinely shared -- the prompt, the taxonomy, the confidence floor, the batching and
/// retry machinery, the safe-description hygiene, and the user-facing regrouping the app's
/// switches depend on -- is reused.
/// </remarks>
public interface ITextContentAnalysisProvider
{
    string ScannerVersion { get; }

    Task<IReadOnlyList<ScanEvent>> AnalyzeCharacterOffsets(
        IReadOnlyList<TranscriptSegment> passages,
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
