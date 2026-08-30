using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

/// <summary>
/// One recorded measurement, and what it was measured on.
/// </summary>
/// <remarks>
/// <see cref="Target"/> is not optional in spirit even though the type allows any string. A
/// synthesis rate from an unnamed instance, or a device rate with no device, cannot be acted on or
/// re-checked later -- it is a number with no claim attached. Every measurement recorded here names
/// what produced it.
/// </remarks>
public sealed record NarrationMeasurement(
    Guid ID,
    string Kind,
    double Value,
    DateTimeOffset MeasuredAt,
    string Target,
    string SoftwareVersion,
    int? RenderAheadWindow = null,
    string? Notes = null);

/// <summary>
/// The measurements the design refuses to guess at.
/// </summary>
/// <remarks>
/// A store rather than configuration because the requirement is that a measurement be recorded
/// together with what it was measured on, and because these values have already proved that
/// derivation is not good enough: three constants in this feature were reasoned from plausible
/// assumptions and each was wrong by 13 to 33 percent, always in the same direction.
/// </remarks>
public interface INarrationMeasurementStore
{
    NarrationMeasurement Record(NarrationMeasurement measurement);

    /// <summary>The most recent measurement of one kind, or null when none has been taken.</summary>
    NarrationMeasurement? Latest(string kind);

    IReadOnlyList<NarrationMeasurement> List(int limit = 200);
}

/// <summary>The kinds of measurement this feature depends on.</summary>
public static class NarrationMeasurementKinds
{
    /// <summary>Characters of text per second of audio, on the premium provider.</summary>
    public const string PremiumSynthesisRate = "premium_synthesis_rate";

    /// <summary>Seconds a scaled-to-zero synthesis endpoint takes to answer.</summary>
    public const string ColdStartDelay = "cold_start_delay";

    /// <summary>Characters per second of audio, on a device's own engine.</summary>
    public const string LocalSynthesisRate = "local_neural_synthesis_rate";

    /// <summary>How much faster than real time a device synthesizes.</summary>
    public const string LocalRealTimeFactor = "local_real_time_factor";

    /// <summary>Whether the primary endpoint's spend lands against the intended account.</summary>
    public const string BillingCoverageVerified = "billing_coverage_verified";

    /// <summary>Bytes of encoded audio per character of text.</summary>
    public const string BytesPerCharacter = "bytes_per_character";

    public static readonly IReadOnlyList<string> All =
    [
        PremiumSynthesisRate, ColdStartDelay, LocalSynthesisRate, LocalRealTimeFactor,
        BillingCoverageVerified, BytesPerCharacter,
    ];

    /// <summary>
    /// The measurements taken so far, as facts rather than as code comments.
    /// </summary>
    /// <remarks>
    /// Seeded so the figures the client's constants were derived from are recoverable from the
    /// system itself, not only from a comment beside the constant. If a later measurement disagrees
    /// with a constant, this is what makes the disagreement visible.
    /// </remarks>
    public static IReadOnlyList<NarrationMeasurement> Seed() =>
    [
        new(
            Guid.Parse("9a1c0000-0000-4000-8000-000000000001"),
            PremiumSynthesisRate,
            18.0,
            new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero),
            "amazon-polly-generative (Ruth, Matthew, Danielle averaged)",
            "polly-generative",
            Notes: "1,080 characters of mixed narration and dialogue. Ruth 17.60, Matthew 19.74, " +
                   "Danielle 16.61. Corrected a derived constant of 13.5, which was 33 percent low."),
        new(
            Guid.Parse("9a1c0000-0000-4000-8000-000000000002"),
            BytesPerCharacter,
            207.349,
            new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero),
            "amazon-polly-generative, mono Opus at 32 kbps requested",
            "polly-generative",
            Notes: "Effective 29.7 kbps against 32 kbps requested, Opus being variable rate. " +
                   "A 400,000-character novel measured 79 MB and 6.2 hours."),
        new(
            Guid.Parse("9a1c0000-0000-4000-8000-000000000003"),
            LocalSynthesisRate,
            18.4,
            new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero),
            "samsung SM-S936U, Android 36, en-US-language, 67 voices available",
            "android-system-tts",
            Notes: "515-character fixed passage. Corrected a derived constant of 16.0, 13 percent " +
                   "low. Within two percent of the premium rate, which is a fact about speech."),
        new(
            Guid.Parse("9a1c0000-0000-4000-8000-000000000004"),
            LocalRealTimeFactor,
            28.2,
            new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero),
            "samsung SM-S936U, Android 36",
            "android-system-tts",
            RenderAheadWindow: 1,
            Notes: "515 characters in 992 ms. Nine times the 3x availability floor, so this device " +
                   "needs the smallest render-ahead window. Says nothing about a low-end device, " +
                   "which is the case the floor exists for and which remains unmeasured."),
    ];
}

/// <summary>
/// Local-development adapter for the same contract the database store implements.
/// </summary>
public sealed class FileNarrationMeasurementStore : INarrationMeasurementStore
{
    private readonly string _path;
    private readonly List<NarrationMeasurement> _measurements = [];
    private readonly object _gate = new();

    public FileNarrationMeasurementStore(AudioChoiceDataPaths paths)
        : this(Path.Combine(paths.Root, "narration-measurements.json"))
    {
    }

    public FileNarrationMeasurementStore(string path)
    {
        _path = path;
        Load();
        // Seeded once, by identifier, so restarting does not accumulate duplicates and a later real
        // measurement of the same kind simply sorts ahead of the seed.
        lock (_gate)
        {
            var known = _measurements.Select(item => item.ID).ToHashSet();
            var missing = NarrationMeasurementKinds.Seed()
                .Where(item => !known.Contains(item.ID))
                .ToArray();
            if (missing.Length > 0)
            {
                _measurements.AddRange(missing);
                Persist();
            }
        }
    }

    public NarrationMeasurement Record(NarrationMeasurement measurement)
    {
        lock (_gate)
        {
            var stored = measurement.ID == Guid.Empty
                ? measurement with { ID = Guid.NewGuid() }
                : measurement;
            _measurements.Add(stored);
            Persist();
            return stored;
        }
    }

    public NarrationMeasurement? Latest(string kind)
    {
        lock (_gate)
        {
            return _measurements
                .Where(item => string.Equals(item.Kind, kind, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.MeasuredAt)
                .FirstOrDefault();
        }
    }

    public IReadOnlyList<NarrationMeasurement> List(int limit = 200)
    {
        lock (_gate)
        {
            return _measurements
                .OrderByDescending(item => item.MeasuredAt)
                .Take(Math.Clamp(limit, 1, 1_000))
                .ToArray();
        }
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var state = System.Text.Json.JsonSerializer
                .Deserialize<List<NarrationMeasurement>>(File.ReadAllText(_path));
            if (state is not null) _measurements.AddRange(state);
        }
        catch (Exception error)
            when (error is System.Text.Json.JsonException or IOException)
        {
            // Measurements are evidence, not operating state. A damaged file must not stop the API
            // from starting, and the seed below restores the recorded figures either way.
        }
    }

    private void Persist()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(_measurements));
        File.Move(temporary, _path, true);
    }
}
