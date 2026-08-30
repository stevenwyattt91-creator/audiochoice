using System.Text.Json;
using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

/// <summary>
/// The premium-synthesis agreement, and who has accepted it.
/// </summary>
/// <remarks>
/// The agreement text lives on the server rather than only in the app so that what somebody
/// agreed to can be produced later, and so a wording change reaches every client at once instead
/// of waiting for an app update.
/// </remarks>
public interface INarrationAgreementStore
{
    NarrationAgreement Current { get; }

    /// <summary>Records an acceptance. Idempotent on the version.</summary>
    NarrationAcknowledgementResponse Accept(Guid userID, string version, string text);

    /// <summary>Whether this account has accepted the version now in force.</summary>
    bool HasAcceptedCurrent(Guid userID);
}

public sealed class FileNarrationAgreementStore : INarrationAgreementStore
{
    private readonly string _path;
    private readonly Dictionary<string, NarrationAcknowledgementResponse> _accepted = [];
    private readonly object _gate = new();

    public FileNarrationAgreementStore(AudioChoiceDataPaths paths)
        : this(Path.Combine(paths.Root, "narration-agreements.json"))
    {
    }

    public FileNarrationAgreementStore(string path)
    {
        _path = path;
        Load();
    }

    /// <summary>
    /// The statement in force.
    /// </summary>
    /// <remarks>
    /// Names each recipient rather than saying "third parties". Somebody deciding whether to send
    /// a book they own is deciding about specific recipients, and a description vague enough to
    /// cover anyone tells them nothing they can weigh.
    ///
    /// It also does not claim the text is never sent, because it is. It states what happens to it:
    /// held for the one request, not written down, not used to train anything. Those are
    /// commitments the implementation keeps and the contract tests check.
    /// </remarks>
    public NarrationAgreement Current { get; } = new(
        Version: "1",
        Text: string.Join(
            "\n\n",
            "The premium voice is made on AudioChoice's servers, not on your phone. To use it, " +
                "each chapter's text is sent to AudioChoice and passed to Amazon Polly to be " +
                "turned into audio.",
            "The text is held only for as long as that one request takes. No copy of your book " +
                "is stored on our servers, and its text is never used to train a model.",
            "The finished audio is sent straight back to your phone and kept there. It is not " +
                "stored in the cloud.",
            "Passages you have filtered are removed before anything is sent, so they never " +
                "leave your device at all.",
            "Your phone's own voice needs none of this and sends nothing anywhere. You can use " +
                "it instead at any time."));

    public NarrationAcknowledgementResponse Accept(Guid userID, string version, string text)
    {
        lock (_gate)
        {
            var key = Key(userID, version);
            // Idempotent, so the offline delivery path can re-send without recording a second
            // acceptance or moving the timestamp of the first.
            if (_accepted.TryGetValue(key, out var existing)) return existing;

            var record = new NarrationAcknowledgementResponse(version, DateTimeOffset.UtcNow);
            _accepted[key] = record;
            Persist();
            return record;
        }
    }

    public bool HasAcceptedCurrent(Guid userID)
    {
        lock (_gate)
        {
            return _accepted.ContainsKey(Key(userID, Current.Version));
        }
    }

    private static string Key(Guid userID, string version) => $"{userID:N}|{version}";

    private void Load()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var state = JsonSerializer
                .Deserialize<Dictionary<string, NarrationAcknowledgementResponse>>(
                    File.ReadAllText(_path));
            if (state is null) return;
            foreach (var entry in state) _accepted[entry.Key] = entry.Value;
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            // A damaged file costs an acceptance being asked for again, which is the safe
            // direction: the alternative is treating an unreadable record as consent.
        }
    }

    private void Persist()
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_accepted));
        File.Move(temporary, _path, true);
    }
}
