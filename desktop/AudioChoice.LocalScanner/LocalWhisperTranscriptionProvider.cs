using System.Text.Json;
using System.Text.Json.Serialization;
using AudioChoice.Api.Processing;
using Microsoft.Extensions.Logging;

namespace AudioChoice.LocalScanner;

/// <summary>
/// Transcribes an audio chunk by running faster-whisper as a short-lived Python subprocess,
/// instead of calling a long-running GPU server over HTTP the way
/// <c>FasterWhisperTranscriptionProvider</c> does for the Lambda worker.
/// </summary>
/// <remarks>
/// A subprocess per chunk, not a persistent server this app talks to over a loopback port, is
/// deliberate: this app runs once per book, by hand, not as a standing service, so there is
/// nothing to keep alive between scans and no idle resource cost while it is not running. The
/// cost is paying Python's own interpreter startup and faster-whisper's model-load time on
/// every chunk rather than once -- acceptable here because a chunk's own transcription time
/// (minutes, on this hardware) dwarfs that startup cost, and because this app's own chunking
/// (see Program.cs) already keeps chunk count modest.
///
/// Implements the same <see cref="ITranscriptionProvider"/> the backend's own transcription
/// providers implement, so <see cref="ScanPipeline"/> drives this exactly the way it drives
/// the Lambda worker's HTTP-based one -- nothing about the pipeline's own chunking, retry, or
/// checkpoint logic needed to change for a local, process-based transport instead of a
/// network one.
/// </remarks>
internal sealed class LocalWhisperTranscriptionProvider(
    string pythonExecutable,
    string transcribeScriptPath,
    string whisperModel,
    ILogger<LocalWhisperTranscriptionProvider> logger) : ITranscriptionProvider
{
    public string ModelName => whisperModel;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<IReadOnlyList<TranscriptSegment>> Transcribe(
        AudioChunk chunk,
        CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Transcribing chunk {StartTime:F0}s-{EndTime:F0}s locally with faster-whisper " +
            "({Model})...",
            chunk.StartTime, chunk.EndTime, whisperModel);

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = pythonExecutable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(transcribeScriptPath);
        startInfo.ArgumentList.Add(chunk.FilePath);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(whisperModel);

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"Could not start the local transcription process ({pythonExecutable}).");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        // transcribe.py logs its own progress to stderr (model loading, warm-up messages),
        // which is useful to see even on success rather than only surfacing it on failure.
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            logger.LogDebug("faster-whisper (chunk {StartTime:F0}s): {Stderr}",
                chunk.StartTime, stderr.Trim());
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Local transcription failed for chunk {chunk.StartTime:F0}s-" +
                $"{chunk.EndTime:F0}s (exit code {process.ExitCode}). {stderr}");
        }

        var parsed = JsonSerializer.Deserialize<TranscribeScriptOutput>(stdout, JsonOptions)
            ?? throw new InvalidOperationException(
                $"Local transcription for chunk {chunk.StartTime:F0}s-{chunk.EndTime:F0}s " +
                "produced no parseable output.");

        // The chunker already overlaps adjacent chunks and reports each chunk's own absolute
        // position in the full audiobook (chunk.StartTime); transcribe.py's own segment times
        // are relative to the chunk file it was handed, so they need shifting back into the
        // whole book's timeline the same way FasterWhisperTranscriptionProvider's HTTP
        // equivalent already does for the Lambda worker.
        return parsed.Segments.Select(segment => new TranscriptSegment(
            segment.StartTime + chunk.StartTime,
            segment.EndTime + chunk.StartTime,
            segment.Text,
            segment.Words?.Select(word => new TranscriptWord(
                word.Text, word.StartTime + chunk.StartTime, word.EndTime + chunk.StartTime))
                .ToArray())).ToArray();
    }

    private sealed record TranscribeScriptOutput(
        [property: JsonPropertyName("segments")] IReadOnlyList<TranscribeScriptSegment> Segments);

    private sealed record TranscribeScriptSegment(
        [property: JsonPropertyName("startTime")] double StartTime,
        [property: JsonPropertyName("endTime")] double EndTime,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("words")] IReadOnlyList<TranscribeScriptWord>? Words);

    private sealed record TranscribeScriptWord(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("startTime")] double StartTime,
        [property: JsonPropertyName("endTime")] double EndTime);
}
