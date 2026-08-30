using System.Text.Json;
using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

/// <summary>
/// Where text-derived filter events are kept, so a book is scanned once and every listener
/// who imports it afterwards is handed the result.
/// </summary>
/// <remarks>
/// Nothing in this contract accepts the book's text. A store that could not write the text
/// even if asked is a stronger guarantee than one that is trusted not to.
///
/// Deliberately not <c>IScanCatalog</c>. That is what the Explore catalogue is built from,
/// and a narrated book must not appear there: the listener supplied the book, and its
/// presence in a public catalogue would advertise what they are reading. Because a text scan
/// has no way to reach that catalogue, the exclusion holds by construction rather than by a
/// filter somebody has to remember to apply.
/// </remarks>
public interface INarrationTextScanStore
{
    /// <summary>The stored scan for this book at this scanner version, if there is one.</summary>
    NarrationTextScan? Load(BookFingerprint fingerprint, string scannerVersion);

    void Save(BookFingerprint fingerprint, NarrationTextScan scan);
}

/// <summary>
/// Validation shared by the file and database stores, so they cannot disagree about what a
/// storable scan is.
/// </summary>
public static class NarrationTextScans
{
    public static string Key(BookFingerprint fingerprint, string scannerVersion) =>
        $"{fingerprint.Version}|{fingerprint.Sha256.ToLowerInvariant()}|" +
        $"{fingerprint.FileSize}|{scannerVersion}";

    /// <summary>
    /// True when a scan is worth recording: a positive text length, and no event pointing
    /// outside it.
    /// </summary>
    /// <remarks>
    /// An out-of-range offset means the two sides disagree about which text was scanned.
    /// Storing one would hand a later reader a mask beyond the end of the book, and because
    /// the text is not kept there would be no way to work out which was wrong.
    /// </remarks>
    public static bool IsStorable(NarrationTextScan scan)
    {
        if (scan.BookTextCharacters <= 0) return false;
        if (string.IsNullOrWhiteSpace(scan.ScannerVersion)) return false;
        foreach (var item in scan.Events)
        {
            if (item.StartTime < 0) return false;
            if (item.EndTime <= item.StartTime) return false;
            if (item.EndTime > scan.BookTextCharacters) return false;
            // Character offsets are whole numbers. A fraction here means a value produced
            // as a time leaked into the character-offset space.
            if (item.StartTime != Math.Floor(item.StartTime)) return false;
            if (item.EndTime != Math.Floor(item.EndTime)) return false;
        }
        return true;
    }
}

/// <summary>
/// Local-development adapter for the same contract the database store implements.
/// </summary>
public sealed class FileNarrationTextScanStore : INarrationTextScanStore
{
    private readonly string _path;
    private readonly Dictionary<string, NarrationTextScan> _scans = [];
    private readonly object _gate = new();

    public FileNarrationTextScanStore(AudioChoiceDataPaths dataPaths)
        : this(Path.Combine(dataPaths.Root, "narration-text-scans.json"))
    {
    }

    public FileNarrationTextScanStore(string path)
    {
        _path = path;
        Load();
    }

    public NarrationTextScan? Load(BookFingerprint fingerprint, string scannerVersion)
    {
        lock (_gate)
        {
            return _scans.GetValueOrDefault(
                NarrationTextScans.Key(fingerprint, scannerVersion));
        }
    }

    public void Save(BookFingerprint fingerprint, NarrationTextScan scan)
    {
        if (!NarrationTextScans.IsStorable(scan)) return;
        lock (_gate)
        {
            _scans[NarrationTextScans.Key(fingerprint, scan.ScannerVersion)] = scan;
            Persist();
        }
    }

    private void Load()
    {
        if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path)) return;
        try
        {
            var state = JsonSerializer
                .Deserialize<Dictionary<string, NarrationTextScan>>(File.ReadAllText(_path));
            if (state is null) return;
            foreach (var entry in state) _scans[entry.Key] = entry.Value;
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            // A damaged cache costs one re-scan. Refusing to start would cost every book.
        }
    }

    private void Persist()
    {
        if (string.IsNullOrWhiteSpace(_path)) return;
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_scans));
        File.Move(temporary, _path, true);
    }
}
