namespace AudioChoice.Api.Processing;

/// <summary>
/// One span of text to be spoken, with the offsets it occupies in the book.
/// </summary>
/// <remarks>
/// The offsets travel with the text so the returned timings can be expressed against the
/// book rather than against the audio. They are character offsets, and they are the client's
/// to interpret: nothing here reads them.
/// </remarks>
public sealed record SpokenUnit(
    int StartCharacter,
    int EndCharacter,
    string Text);

/// <summary>Where one unit's audio sits inside a chapter, measured from that chapter's start.</summary>
/// <remarks>
/// Chapter-relative on purpose, matching how the client stores timelines: re-rendering one
/// chapter then invalidates no other chapter's timings.
/// </remarks>
public sealed record UnitTiming(
    int StartCharacter,
    int EndCharacter,
    double StartSeconds,
    double EndSeconds);

/// <summary>
/// A chapter's worth of work.
/// </summary>
/// <remarks>
/// The <c>ToString</c> override is the point of this being a record with a hand-written one.
/// A generated <c>ToString</c> prints every member, so a single log scope holding this would
/// write a chapter of a novel into a retained, searchable sink -- the exact disclosure the
/// feature promises not to make, arriving by accident. The safe rendering is the default
/// rather than something each call site has to remember.
/// </remarks>
public sealed record ChapterSynthesisInput(
    Guid JobID,
    int ChapterIndex,
    string VoiceID,
    string? Language,
    IReadOnlyList<SpokenUnit> Units)
{
    /// <summary>Total characters to be spoken, which is what bounds the work.</summary>
    public int CharacterCount => Units.Sum(unit => unit.Text.Length);

    public override string ToString() =>
        $"ChapterSynthesisInput {{ JobID = {JobID}, ChapterIndex = {ChapterIndex}, " +
        $"VoiceID = {VoiceID}, Language = {Language}, Units = {Units.Count}, " +
        $"Characters = {CharacterCount} }}";
}

/// <summary>One chapter's finished audio and its per-unit timings.</summary>
public sealed record SynthesizedChapter(
    Guid JobID,
    int ChapterIndex,
    string Provider,
    string ModelVersion,
    string VoiceID,
    double DurationSeconds,
    IReadOnlyList<UnitTiming> Timings,
    byte[] Audio)
{
    public override string ToString() =>
        $"SynthesizedChapter {{ JobID = {JobID}, ChapterIndex = {ChapterIndex}, " +
        $"Provider = {Provider}, ModelVersion = {ModelVersion}, VoiceID = {VoiceID}, " +
        $"DurationSeconds = {DurationSeconds:F1}, Timings = {Timings.Count}, " +
        $"AudioBytes = {Audio.Length} }}";
}

/// <summary>A voice a listener can choose, with a fixed pre-rendered sample.</summary>
/// <remarks>
/// The sample is a pre-rendered asset rather than synthesised on demand, so browsing voices
/// costs nothing and sends no text anywhere.
/// </remarks>
public sealed record NarrationVoice(
    string VoiceID,
    string DisplayName,
    string Language,
    string Provider,
    string SampleUrl);

/// <summary>
/// Turns text into a chapter of audio.
/// </summary>
/// <remarks>
/// Shaped after <c>ITranscriptionProvider</c>, which it is the mirror of, so the two read the
/// same way in the container and in the pipeline. Deliberately a separate interface: nothing
/// here may reach the transcription lane, and a shared abstraction is how that boundary would
/// erode.
/// </remarks>
public interface ISynthesisProvider
{
    /// <summary>Recorded per chapter, so a book spanning a provider change stays explicable.</summary>
    string Provider { get; }

    string ModelVersion { get; }

    /// <summary>Voices this provider offers, for the selection surface.</summary>
    Task<IReadOnlyList<NarrationVoice>> Voices(CancellationToken cancellationToken);

    /// <summary>
    /// Synthesises one chapter.
    /// </summary>
    /// <remarks>
    /// Throws to signal failure, which the router reads as "try the fallback". Returning null
    /// would make an unavailable endpoint indistinguishable from a chapter with nothing to say.
    /// </remarks>
    Task<SynthesizedChapter> Synthesize(
        ChapterSynthesisInput input,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether this provider is currently able to accept work.
    /// </summary>
    /// <remarks>
    /// Exists for the scaled-to-zero case: an endpoint that is provisioning is not failing,
    /// but it is also not going to answer quickly, and the router needs to tell those apart
    /// before spending a listener's wait on it.
    /// </remarks>
    Task<bool> IsAvailable(CancellationToken cancellationToken) => Task.FromResult(true);
}
