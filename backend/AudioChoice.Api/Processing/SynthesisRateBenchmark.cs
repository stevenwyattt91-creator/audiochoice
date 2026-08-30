namespace AudioChoice.Api.Processing;

/// <summary>
/// One provider's measured synthesis rate.
/// </summary>
public sealed record SynthesisRateMeasurement(
    string Provider,
    string ModelVersion,
    string VoiceID,
    int Characters,
    double AudioSeconds,
    double ElapsedSeconds,
    long AudioBytes)
{
    /// <summary>Characters of text per second of audio. The speaking rate.</summary>
    public double CharactersPerSecondOfAudio =>
        AudioSeconds <= 0 ? 0 : Characters / AudioSeconds;

    /// <summary>How much faster than real time the provider produced it.</summary>
    public double RealTimeFactor => ElapsedSeconds <= 0 ? 0 : AudioSeconds / ElapsedSeconds;

    /// <summary>Bytes of encoded audio per character, which is what sizes a book on a device.</summary>
    public double BytesPerCharacter => Characters <= 0 ? 0 : AudioBytes / (double)Characters;

    public double EffectiveBitrateKilobits =>
        AudioSeconds <= 0 ? 0 : AudioBytes * 8 / AudioSeconds / 1_000;

    public override string ToString() =>
        $"{Provider}/{VoiceID}: {CharactersPerSecondOfAudio:F2} chars/sec spoken, " +
        $"{RealTimeFactor:F1}x real time, {BytesPerCharacter:F1} bytes/char, " +
        $"{EffectiveBitrateKilobits:F1} kbps ({Characters} chars, {AudioSeconds:F1}s audio)";
}

/// <summary>
/// Measures a synthesis provider's actual rate.
/// </summary>
/// <remarks>
/// A permanent tool rather than a throwaway script, because the numbers it produces are the ones
/// three separate constants in this feature were derived wrongly from. Each derivation sounded
/// reasonable — "150 words a minute", "a considered narrator's pace" — and each was 13 to 33
/// percent out, always over-estimating. The only thing that caught them was synthesizing a passage
/// and timing the result.
///
/// Run against a real provider, so it needs credentials and costs a few pence. It is deliberately
/// not part of the contract test suite: those must pass with no network and no AWS account.
/// </remarks>
public sealed class SynthesisRateBenchmark(ISynthesisProvider provider)
{
    /// <summary>
    /// The passage every provider is measured on.
    /// </summary>
    /// <remarks>
    /// Fixed in source rather than generated, because a rate is only comparable across providers
    /// and voices if they read the same words. Mixed narration and dialogue on purpose:
    /// punctuation-heavy text synthesizes differently from flat prose, and a novel is both.
    /// </remarks>
    public static readonly IReadOnlyList<string> Passage =
    [
        "She had not expected him to be waiting.",
        "The rain had stopped an hour ago, and the street still held that washed, expectant " +
            "quiet that comes after a long storm has finally moved on towards the sea.",
        "\"You came,\" he said.",
        "Something in his voice made her stop three steps short of him, close enough to see the " +
            "water still beading on his collar.",
        "\"Did you think I wouldn't?\"",
        "She had rehearsed a dozen versions of this conversation on the way over, and every " +
            "single one of them had deserted her the moment she turned the corner.",
        "He looked at her for a long moment.",
        "\"No,\" he admitted. \"I suppose I didn't.\"",
        "Later, when she tried to remember what they had actually said to each other, she found " +
            "she could recall only the sound of water dripping from the awning above them.",
        "It was, she thought afterwards, the first honest thing either of them had done in months.",
        "The cafe behind him was closing; a woman inside stacked chairs with the unhurried " +
            "competence of someone who had done it ten thousand times.",
        "\"Walk with me,\" she said, and he did.",
    ];

    public static int PassageCharacters => Passage.Sum(passage => passage.Length);

    /// <summary>Measures one voice.</summary>
    public async Task<SynthesisRateMeasurement> Measure(
        string voiceID,
        CancellationToken cancellationToken)
    {
        var units = new List<SpokenUnit>();
        var cursor = 0;
        foreach (var passage in Passage)
        {
            units.Add(new SpokenUnit(cursor, cursor + passage.Length, passage));
            // One for the space that would separate them in a book, so the offsets read like a
            // real document's rather than being packed together.
            cursor += passage.Length + 1;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var chapter = await provider.Synthesize(
            new ChapterSynthesisInput(Guid.NewGuid(), 0, voiceID, "en-US", units),
            cancellationToken);
        var elapsed = DateTimeOffset.UtcNow - startedAt;

        return new SynthesisRateMeasurement(
            Provider: chapter.Provider,
            ModelVersion: chapter.ModelVersion,
            VoiceID: voiceID,
            Characters: PassageCharacters,
            AudioSeconds: chapter.DurationSeconds,
            ElapsedSeconds: elapsed.TotalSeconds,
            AudioBytes: chapter.Audio.Length);
    }

    /// <summary>
    /// Measures several voices and averages them.
    /// </summary>
    /// <remarks>
    /// An average rather than one voice's figure, because the spread between voices is real: the
    /// three Polly voices measured on 2026-08-29 ranged from 16.61 to 19.74 characters a second, a
    /// nineteen percent spread. Taking one voice's number would have been a measurement of that
    /// voice rather than of the provider.
    /// </remarks>
    public async Task<IReadOnlyList<SynthesisRateMeasurement>> MeasureAll(
        IReadOnlyList<string> voiceIDs,
        CancellationToken cancellationToken)
    {
        var results = new List<SynthesisRateMeasurement>();
        foreach (var voiceID in voiceIDs)
        {
            results.Add(await Measure(voiceID, cancellationToken));
        }
        return results;
    }

    /// <summary>Turns measurements into the records the measurement store keeps.</summary>
    public static IReadOnlyList<Services.NarrationMeasurement> AsRecords(
        IReadOnlyList<SynthesisRateMeasurement> measurements,
        DateTimeOffset measuredAt)
    {
        if (measurements.Count == 0) return [];
        var provider = measurements[0].Provider;
        var model = measurements[0].ModelVersion;
        var target = $"{provider} ({string.Join(", ", measurements.Select(item => item.VoiceID))} averaged)";

        return
        [
            new Services.NarrationMeasurement(
                Guid.NewGuid(),
                Services.NarrationMeasurementKinds.PremiumSynthesisRate,
                measurements.Average(item => item.CharactersPerSecondOfAudio),
                measuredAt,
                target,
                model,
                Notes: $"{PassageCharacters} characters of mixed narration and dialogue. " +
                       string.Join("; ", measurements.Select(item =>
                           $"{item.VoiceID} {item.CharactersPerSecondOfAudio:F2}"))),
            new Services.NarrationMeasurement(
                Guid.NewGuid(),
                Services.NarrationMeasurementKinds.BytesPerCharacter,
                measurements.Average(item => item.BytesPerCharacter),
                measuredAt,
                target,
                model,
                Notes: $"Effective {measurements.Average(item => item.EffectiveBitrateKilobits):F1} kbps."),
        ];
    }
}
