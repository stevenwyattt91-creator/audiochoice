using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

/// <summary>
/// Which provider and voice produced one chapter of one listener's book.
/// </summary>
/// <remarks>
/// Recorded per chapter rather than per book, and that is the whole point of the type. A book
/// legitimately holds audio from more than one voice: a premium entitlement that lapses mid-book
/// keeps the chapters already made and finishes the rest on the device's own voice. Without a
/// per-chapter record, that book's audio would be inexplicable — a listener asking why chapter
/// eight sounds different from chapter seven would have no answer available.
///
/// <see cref="ObjectPath"/> is deliberately not a URL to stored audio. Chapter audio is returned to
/// the device and kept nowhere on the server, so this names where it went, not where it is.
/// </remarks>
public sealed record NarrationChapterRender(
    Guid ID,
    Guid UserID,
    BookFingerprint Fingerprint,
    int ChapterIndex,
    string VoiceID,
    string Provider,
    string ModelVersion,
    double DurationSeconds,
    string ObjectPath,
    DateTimeOffset CreatedAt);

/// <summary>
/// Where per-chapter render records are kept.
/// </summary>
/// <remarks>
/// Records metadata about audio, never audio and never text. There is no method here that could
/// accept a character of Spoken_Text, which is a stronger guarantee than one that is trusted not
/// to be given any.
/// </remarks>
public interface INarrationRenderStore
{
    NarrationChapterRender Record(NarrationChapterRender render);

    /// <summary>Every chapter recorded for one listener's book, in chapter order.</summary>
    IReadOnlyList<NarrationChapterRender> ForBook(Guid userID, string sha256);
}

public sealed class FileNarrationRenderStore : INarrationRenderStore
{
    private readonly string _path;
    private readonly List<NarrationChapterRender> _renders = [];
    private readonly object _gate = new();

    public FileNarrationRenderStore(AudioChoiceDataPaths paths)
        : this(Path.Combine(paths.Root, "narration-renders.json"))
    {
    }

    public FileNarrationRenderStore(string path)
    {
        _path = path;
        Load();
    }

    public NarrationChapterRender Record(NarrationChapterRender render)
    {
        lock (_gate)
        {
            var stored = render.ID == Guid.Empty ? render with { ID = Guid.NewGuid() } : render;

            // One row per listener, book, chapter and voice. Re-rendering the same chapter with the
            // same voice replaces rather than accumulates, matching the unique key the database
            // enforces — so the two stores cannot disagree about how many times a chapter was made.
            _renders.RemoveAll(existing =>
                existing.UserID == stored.UserID &&
                string.Equals(existing.Fingerprint.Sha256, stored.Fingerprint.Sha256,
                    StringComparison.OrdinalIgnoreCase) &&
                existing.ChapterIndex == stored.ChapterIndex &&
                existing.VoiceID == stored.VoiceID);

            _renders.Add(stored);
            Persist();
            return stored;
        }
    }

    public IReadOnlyList<NarrationChapterRender> ForBook(Guid userID, string sha256)
    {
        lock (_gate)
        {
            return _renders
                .Where(render =>
                    render.UserID == userID &&
                    string.Equals(render.Fingerprint.Sha256, sha256, StringComparison.OrdinalIgnoreCase))
                .OrderBy(render => render.ChapterIndex)
                .ToArray();
        }
    }

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var state = System.Text.Json.JsonSerializer
                .Deserialize<List<NarrationChapterRender>>(File.ReadAllText(_path));
            if (state is not null) _renders.AddRange(state);
        }
        catch (Exception error)
            when (error is System.Text.Json.JsonException or IOException)
        {
            // These records explain audio the listener already has; losing them costs an
            // explanation, not the audio. Refusing to start would cost far more.
        }
    }

    private void Persist()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(_renders));
        File.Move(temporary, _path, true);
    }
}

/// <summary>
/// What a book's render records say about which voices made it.
/// </summary>
public static class NarrationRenderSummary
{
    /// <summary>
    /// Whether a book was made by more than one voice.
    /// </summary>
    /// <remarks>
    /// The question a listener is really asking when a chapter sounds different. Answered from the
    /// records rather than from the currently selected voice, because the selected voice is what
    /// will make the *next* chapter and says nothing about the ones already made.
    /// </remarks>
    public static bool SpansSeveralVoices(IReadOnlyList<NarrationChapterRender> renders) =>
        renders.Select(render => render.VoiceID).Distinct(StringComparer.Ordinal).Count() > 1;

    /// <summary>Chapters made by each voice, so a mixed book can be described precisely.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<int>> ChaptersByVoice(
        IReadOnlyList<NarrationChapterRender> renders) =>
        renders
            .GroupBy(render => render.VoiceID, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<int>)group.Select(render => render.ChapterIndex)
                    .Order()
                    .ToArray(),
                StringComparer.Ordinal);

    /// <summary>
    /// The book's duration, from the chapters actually made.
    /// </summary>
    /// <remarks>
    /// Sums the records rather than estimating from the plan, so a partly-rendered book reports the
    /// audio that exists. A duration covering chapters that have not been made would be a promise
    /// the book cannot keep.
    /// </remarks>
    public static double RenderedDurationSeconds(IReadOnlyList<NarrationChapterRender> renders) =>
        renders.Sum(render => render.DurationSeconds);
}
