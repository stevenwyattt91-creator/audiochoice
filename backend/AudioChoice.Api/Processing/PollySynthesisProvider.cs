using System.Globalization;
using Amazon.Polly;
using Amazon.Polly.Model;

namespace AudioChoice.Api.Processing;

/// <summary>
/// Premium narration on Amazon Polly's generative engine.
/// </summary>
/// <remarks>
/// The launch implementation and, for now, the only one. Polly needs no infrastructure
/// AudioChoice operates, so a book costs what its characters cost and an idle month costs
/// nothing -- against roughly a thousand dollars a month for a GPU endpoint sitting warm.
///
/// One request per Narration_Unit rather than one per chapter. That looks wasteful and is the
/// point: the response to a single unit gives that unit's exact duration by measuring the audio
/// it returned, so the per-unit timings the reader highlights from are measured rather than
/// estimated. The alternative is one chapter-sized request plus speech-mark parsing to find
/// unit boundaries inside it, which means trusting a second output format to agree with the
/// first about where words are. Polly charges per character either way, so the extra requests
/// cost nothing beyond latency.
/// </remarks>
public sealed class PollySynthesisProvider(
    IAmazonPolly polly,
    IProcessRunner processRunner,
    FfmpegAudioChunkerOptions ffmpeg,
    ILogger<PollySynthesisProvider> logger) : ISynthesisProvider
{
    public string Provider => "polly";

    /// <summary>
    /// Recorded per chapter, so a book spanning an engine change stays explicable.
    /// </summary>
    /// <remarks>
    /// Names the engine rather than a version number, because Polly does not expose one. If a
    /// listener reports that a chapter sounds different from its neighbours, this is what says
    /// whether it was made by a different engine.
    /// </remarks>
    public string ModelVersion => "polly-generative";

    /// <summary>
    /// Mono, because narration is one voice and stereo would double the bytes to say so.
    /// </summary>
    public const string AudioChannels = "1";

    /// <summary>32 kbps Opus. Speech at this rate is transparent; music would not be.</summary>
    public const string AudioBitrate = "32k";

    /// <summary>
    /// Polly's own output format for the per-unit requests.
    /// </summary>
    /// <remarks>
    /// PCM rather than MP3, so concatenation is a byte-level join with no decode step and no
    /// codec-boundary artefacts between units. The single encode to Opus happens once, over the
    /// whole joined chapter.
    /// </remarks>
    private const int PollySampleRate = 16_000;

    public async Task<IReadOnlyList<NarrationVoice>> Voices(CancellationToken cancellationToken)
    {
        var response = await polly.DescribeVoicesAsync(
            new DescribeVoicesRequest { Engine = Engine.Generative },
            cancellationToken);

        return response.Voices
            // English only for now. Offering a voice that cannot read the listener's book is
            // worse than offering fewer.
            .Where(voice => voice.LanguageCode.Value.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            .Select(voice => new NarrationVoice(
                VoiceID: voice.Id.Value,
                DisplayName: voice.Name,
                Language: voice.LanguageCode.Value,
                Provider: Provider,
                // A fixed pre-rendered asset, not synthesised on demand: browsing voices then
                // costs nothing and sends no text anywhere.
                SampleUrl: $"/narration-samples/{voice.Id.Value.ToLowerInvariant()}.opus"))
            .OrderBy(voice => voice.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<bool> IsAvailable(CancellationToken cancellationToken)
    {
        // A managed service with no endpoint to warm. Asked anyway, because the router treats an
        // unavailable primary differently from a failing one, and a credentials or region problem
        // shows up here as a clean "not available" rather than as a failed chapter.
        try
        {
            await polly.DescribeVoicesAsync(
                new DescribeVoicesRequest { Engine = Engine.Generative }, cancellationToken);
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(error, "Amazon Polly is not reachable.");
            return false;
        }
    }

    public async Task<SynthesizedChapter> Synthesize(
        ChapterSynthesisInput input,
        CancellationToken cancellationToken)
    {
        if (input.Units.Count == 0)
        {
            // A chapter whose every unit was filtered away. Legitimate, and it must produce a
            // real result with no audio rather than an error: the chapter counts as rendered and
            // adds nothing to the book's duration.
            return new SynthesizedChapter(
                input.JobID, input.ChapterIndex, Provider, ModelVersion, input.VoiceID,
                0, [], []);
        }

        var workingDirectory = Directory.CreateTempSubdirectory("audiochoice-narration-");
        try
        {
            var timings = new List<UnitTiming>(input.Units.Count);
            var pcmPath = Path.Combine(workingDirectory.FullName, "chapter.pcm");
            var cursorSeconds = 0.0;

            // Appended in order to one raw stream. Timings are built here, where the samples are
            // actually being counted, rather than inferred afterwards from the encoded file --
            // the encoder pads and the container rounds, so measuring after the fact would drift.
            await using (var chapter = File.Create(pcmPath))
            {
                foreach (var unit in input.Units)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var samples = await SynthesizeUnit(input, unit, cancellationToken);
                    var seconds = samples.Length / (double)(PollySampleRate * BytesPerSample);

                    timings.Add(new UnitTiming(
                        unit.StartCharacter,
                        unit.EndCharacter,
                        cursorSeconds,
                        cursorSeconds + seconds));
                    cursorSeconds += seconds;

                    await chapter.WriteAsync(samples, cancellationToken);
                }
            }

            var audio = await EncodeOpus(pcmPath, workingDirectory.FullName, cancellationToken);

            logger.LogInformation(
                "Polly synthesized chapter {ChapterIndex} as {UnitCount} units, " +
                "{DurationSeconds:F1} seconds, {AudioBytes} bytes.",
                input.ChapterIndex, input.Units.Count, cursorSeconds, audio.Length);

            return new SynthesizedChapter(
                input.JobID, input.ChapterIndex, Provider, ModelVersion, input.VoiceID,
                cursorSeconds, timings, audio);
        }
        finally
        {
            // The scratch directory holds raw audio of the listener's book. It goes whether or
            // not the chapter succeeded, and a failure to clean up is logged rather than thrown
            // so it cannot mask the real error.
            try
            {
                workingDirectory.Delete(recursive: true);
            }
            catch (Exception error)
            {
                logger.LogWarning(
                    error, "Could not remove the narration scratch directory {Directory}.",
                    workingDirectory.FullName);
            }
        }
    }

    /// <summary>16-bit samples.</summary>
    private const int BytesPerSample = 2;

    private async Task<byte[]> SynthesizeUnit(
        ChapterSynthesisInput input,
        SpokenUnit unit,
        CancellationToken cancellationToken)
    {
        var response = await polly.SynthesizeSpeechAsync(
            new SynthesizeSpeechRequest
            {
                Engine = Engine.Generative,
                VoiceId = input.VoiceID,
                Text = unit.Text,
                OutputFormat = OutputFormat.Pcm,
                SampleRate = PollySampleRate.ToString(CultureInfo.InvariantCulture),
            },
            cancellationToken);

        using var buffer = new MemoryStream();
        await response.AudioStream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    /// <summary>
    /// Encodes the joined chapter once, to mono Opus.
    /// </summary>
    /// <remarks>
    /// One encode over the whole chapter rather than per unit. Encoding each unit separately then
    /// concatenating the results would put a codec boundary between every sentence, and Opus
    /// pads each stream with priming samples -- so the audible result is a faint click at every
    /// unit join, and the timings measured above would no longer match the file.
    /// </remarks>
    private async Task<byte[]> EncodeOpus(
        string pcmPath,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var outputPath = Path.Combine(workingDirectory, "chapter.opus");
        var result = await processRunner.Run(
            ffmpeg.FfmpegPath,
            [
                "-hide_banner",
                "-loglevel", "error",
                "-nostdin",
                "-y",
                // Describes the raw stream, which carries no header of its own.
                "-f", "s16le",
                "-ar", PollySampleRate.ToString(CultureInfo.InvariantCulture),
                "-ac", AudioChannels,
                "-i", pcmPath,
                "-c:a", "libopus",
                "-b:a", AudioBitrate,
                "-ac", AudioChannels,
                // Tells the encoder this is speech, which is what lets 32 kbps sound clean.
                "-application", "voip",
                outputPath,
            ],
            cancellationToken);

        if (result.ExitCode != 0 || !File.Exists(outputPath))
        {
            throw new InvalidOperationException(
                $"Encoding narration audio failed with exit code {result.ExitCode}. " +
                result.StandardError.Trim());
        }

        return await File.ReadAllBytesAsync(outputPath, cancellationToken);
    }
}
