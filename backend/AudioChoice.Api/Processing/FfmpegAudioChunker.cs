using System.Diagnostics;
using System.Globalization;

namespace AudioChoice.Api.Processing;

public sealed record ProcessExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public interface IProcessRunner
{
    Task<ProcessExecutionResult> Run(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessExecutionResult> Run(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        if (!process.Start())
        {
            throw new InvalidOperationException(
                $"Could not start {executable}.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(
            cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(
            cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        return new ProcessExecutionResult(
            process.ExitCode,
            await standardOutput,
            await standardError);
    }
}

public sealed class FfmpegAudioChunkerOptions
{
    public string FfmpegPath { get; init; } = "ffmpeg";
    public string FfprobePath { get; init; } = "ffprobe";
    public double ChunkDurationSeconds { get; init; } = 600;
    public double OverlapSeconds { get; init; } = 2;
    public int SampleRate { get; init; } = 16_000;
    public double MaximumInputDurationSeconds { get; init; } = 108_000;
}

public sealed class FfmpegAudioChunker(
    IProcessRunner processRunner,
    FfmpegAudioChunkerOptions options) : IAudioChunker, IPreMaterializedAudioChunker
{
    public async Task<IReadOnlyList<AudioChunk>> Materialize(
        string audioFilePath, CancellationToken cancellationToken)
    {
        ValidateOptions();
        if (!File.Exists(audioFilePath)) throw new FileNotFoundException(
            "The private uploaded audiobook was not found.", audioFilePath);
        var duration = await ProbeDuration(audioFilePath, cancellationToken);
        if (duration > options.MaximumInputDurationSeconds) throw new InvalidOperationException(
            $"FFprobe reported {duration:F3} seconds, above the configured pre-processing limit.");
        var folder = Path.Combine(Path.GetTempPath(), "AudioChoice", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var chunks = new List<AudioChunk>();
        try
        {
            var step = options.ChunkDurationSeconds - options.OverlapSeconds;
            for (var index = 0; ; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var start = index * step;
                if (start >= duration) break;
                var end = Math.Min(start + options.ChunkDurationSeconds, duration);
                var path = Path.Combine(folder, $"chunk-{index:D6}.wav");
                await CreateChunk(audioFilePath, path, start, end - start, cancellationToken);
                chunks.Add(new AudioChunk(path, start, end));
                if (end >= duration) break;
            }
            var remaining = chunks.Count;
            for (var index = 0; index < chunks.Count; index++)
            {
                var chunk = chunks[index];
                var path = chunk.FilePath;
                chunks[index] = chunk with { Cleanup = () =>
                {
                    if (File.Exists(path)) File.Delete(path);
                    if (Interlocked.Decrement(ref remaining) == 0 && Directory.Exists(folder))
                        Directory.Delete(folder, true);
                }};
            }
            return chunks;
        }
        catch { if (Directory.Exists(folder)) Directory.Delete(folder, true); throw; }
    }
    public async IAsyncEnumerable<AudioChunk> CreateChunks(
        string audioFilePath,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        ValidateOptions();

        if (!File.Exists(audioFilePath))
        {
            throw new FileNotFoundException(
                "The private uploaded audiobook was not found.",
                audioFilePath);
        }

        var duration = await ProbeDuration(
            audioFilePath,
            cancellationToken);

        if (duration > options.MaximumInputDurationSeconds)
        {
            throw new InvalidOperationException(
                $"FFprobe reported {duration:F3} seconds, above the configured " +
                $"pre-processing limit of {options.MaximumInputDurationSeconds:F3} seconds.");
        }

        var temporaryFolder = Path.Combine(
            Path.GetTempPath(),
            "AudioChoice",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(temporaryFolder);

        try
        {
            var step = options.ChunkDurationSeconds - options.OverlapSeconds;
            var chunkIndex = 0;

            for (double startTime = 0;
                 startTime < duration;
                 startTime += step)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunkDuration = Math.Min(
                    options.ChunkDurationSeconds,
                    duration - startTime);

                var endTime = startTime + chunkDuration;
                var chunkPath = Path.Combine(
                    temporaryFolder,
                    $"chunk-{chunkIndex:D6}.wav");

                await CreateChunk(
                    audioFilePath,
                    chunkPath,
                    startTime,
                    chunkDuration,
                    cancellationToken);

                try
                {
                    yield return new AudioChunk(
                        chunkPath,
                        startTime,
                        endTime,
                        () => { if (File.Exists(chunkPath)) File.Delete(chunkPath); });
                }
                finally
                {
                    File.Delete(chunkPath);
                }

                chunkIndex += 1;

                if (endTime >= duration)
                {
                    break;
                }
            }
        }
        finally
        {
            if (Directory.Exists(temporaryFolder))
            {
                Directory.Delete(temporaryFolder, recursive: true);
            }
        }
    }

    private async Task<double> ProbeDuration(
        string audioFilePath,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.Run(
            options.FfprobePath,
            [
                "-v", "error",
                "-select_streams", "a:0",
                "-show_entries", "stream=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                audioFilePath
            ],
            cancellationToken);

        if (result.ExitCode != 0 ||
            !double.TryParse(
                result.StandardOutput.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var duration) ||
            duration <= 0)
        {
            throw new InvalidOperationException(
                $"FFprobe could not determine audiobook duration: {result.StandardError}");
        }

        return duration;
    }

    private async Task CreateChunk(
        string audioFilePath,
        string chunkPath,
        double startTime,
        double duration,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.Run(
            options.FfmpegPath,
            [
                "-hide_banner",
                "-loglevel", "error",
                "-nostdin",
                "-y",
                "-ss", startTime.ToString(CultureInfo.InvariantCulture),
                "-t", duration.ToString(CultureInfo.InvariantCulture),
                "-i", audioFilePath,
                "-vn",
                "-ac", "1",
                "-ar", options.SampleRate.ToString(CultureInfo.InvariantCulture),
                "-c:a", "pcm_s16le",
                chunkPath
            ],
            cancellationToken);

        if (result.ExitCode != 0 || !File.Exists(chunkPath))
        {
            throw new InvalidOperationException(
                $"FFmpeg could not create an audio chunk: {result.StandardError}");
        }
    }

    private void ValidateOptions()
    {
        if (options.ChunkDurationSeconds <= 0 ||
            options.OverlapSeconds < 0 ||
            options.OverlapSeconds >= options.ChunkDurationSeconds ||
            options.SampleRate <= 0 ||
            options.MaximumInputDurationSeconds <= 0)
        {
            throw new InvalidOperationException(
                "FFmpeg chunking configuration is invalid.");
        }
    }
}
