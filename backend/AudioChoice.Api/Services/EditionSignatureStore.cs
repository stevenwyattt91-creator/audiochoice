using System.Collections.Concurrent;
using System.Text.Json;
using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

/// <summary>
/// Holds the client-reported identity evidence for each file fingerprint.
/// </summary>
public interface IEditionSignatureStore
{
    EditionSignature? Find(BookFingerprint fingerprint);

    /// <summary>
    /// Records a signature, merging field by field so a client that knows less than
    /// an earlier one cannot erase evidence already gathered.
    /// </summary>
    void Record(BookFingerprint fingerprint, EditionSignature signature);
}

/// <summary>
/// JSON-backed signature store, alongside the other AudioChoice data files.
/// </summary>
/// <remarks>
/// Unlike <see cref="FileEditionAliasStore"/> these facts are not derivable
/// server-side, so losing this file is a real loss -- matching degrades to comparing
/// fingerprint metadata until a client reports again. It sits on the same persistent
/// mount as the transcripts it helps locate, and it is written atomically. The
/// tradeoff bought here is that no schema migration is required and every matching
/// rule stays testable without a database.
/// </remarks>
public sealed class FileEditionSignatureStore : IEditionSignatureStore
{
    private readonly string _storagePath;
    private readonly ConcurrentDictionary<string, EditionSignature> _signatures = new();
    private readonly object _persistenceLock = new();

    public FileEditionSignatureStore(AudioChoiceDataPaths dataPaths)
        : this(dataPaths.EditionSignatures)
    {
    }

    public FileEditionSignatureStore(string storagePath)
    {
        _storagePath = storagePath;
        Load();
    }

    public EditionSignature? Find(BookFingerprint fingerprint) =>
        _signatures.GetValueOrDefault(InMemoryScanCatalog.FingerprintKey(fingerprint));

    public void Record(BookFingerprint fingerprint, EditionSignature signature)
    {
        if (signature.IsEmpty) return;

        var key = InMemoryScanCatalog.FingerprintKey(fingerprint);
        var existing = _signatures.GetValueOrDefault(key);
        var merged = existing is null ? signature : Merge(existing, signature);
        if (existing is not null && SameAs(existing, merged)) return;

        _signatures[key] = merged;
        Persist();
    }

    /// <summary>
    /// Prefers whichever side actually states a value. A newer report that omits a
    /// field is treated as silence, not as a correction.
    /// </summary>
    private static EditionSignature Merge(EditionSignature existing, EditionSignature incoming) =>
        new(
            ProductIdentifier: Prefer(incoming.ProductIdentifier, existing.ProductIdentifier),
            Narrator: Prefer(incoming.Narrator, existing.Narrator),
            ChapterOffsetSeconds: incoming.ChapterOffsetSeconds is { Count: > 0 }
                ? incoming.ChapterOffsetSeconds
                : existing.ChapterOffsetSeconds);

    private static string? Prefer(string? incoming, string? existing) =>
        string.IsNullOrWhiteSpace(incoming) ? existing : incoming;

    /// <summary>
    /// Record equality compares the chapter list by reference, which would report a
    /// change on every identical report and rewrite the file each time.
    /// </summary>
    private static bool SameAs(EditionSignature left, EditionSignature right) =>
        left.ProductIdentifier == right.ProductIdentifier
        && left.Narrator == right.Narrator
        && (left.ChapterOffsetSeconds ?? []).SequenceEqual(right.ChapterOffsetSeconds ?? []);

    private void Load()
    {
        if (string.IsNullOrWhiteSpace(_storagePath) || !File.Exists(_storagePath)) return;
        try
        {
            var state = JsonSerializer.Deserialize<Dictionary<string, EditionSignature>>(
                File.ReadAllText(_storagePath));
            if (state is null) return;
            foreach (var entry in state) _signatures[entry.Key] = entry.Value;
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            // Degrade to metadata-only matching rather than refusing to start.
        }
    }

    private void Persist()
    {
        if (string.IsNullOrWhiteSpace(_storagePath)) return;
        lock (_persistenceLock)
        {
            var directory = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporary = _storagePath + ".tmp";
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(new Dictionary<string, EditionSignature>(_signatures)));
            File.Move(temporary, _storagePath, true);
        }
    }
}
