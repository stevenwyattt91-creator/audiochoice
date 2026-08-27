using System.Collections.Concurrent;
using System.Text.Json;
using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

/// <summary>
/// Records which file fingerprints are known to be the same recording.
/// </summary>
public interface IEditionAliasStore
{
    /// <summary>Other file identities linked to this one, nearest first.</summary>
    IReadOnlyList<BookFingerprint> Aliases(BookFingerprint fingerprint);

    /// <summary>Links two fingerprints in both directions. Idempotent.</summary>
    void Link(BookFingerprint first, BookFingerprint second);
}

/// <summary>
/// JSON-backed alias store living beside the other AudioChoice data files.
/// </summary>
/// <remarks>
/// Deliberately not a database table. Every entry here is a *cache of a derivable
/// fact* -- <see cref="EditionResolver"/> can rediscover any link by comparing
/// edition metadata -- so losing this file costs one slower lookup, not data. That
/// property is what makes a plain file safe on the shared Azure file mount, where
/// the API and the scan worker can both write it.
/// </remarks>
public sealed class FileEditionAliasStore : IEditionAliasStore
{
    private readonly string _storagePath;
    private readonly ConcurrentDictionary<string, List<BookFingerprint>> _aliases = new();
    private readonly object _persistenceLock = new();

    public FileEditionAliasStore(AudioChoiceDataPaths dataPaths)
        : this(dataPaths.EditionAliases)
    {
    }

    public FileEditionAliasStore(string storagePath)
    {
        _storagePath = storagePath;
        Load();
    }

    public IReadOnlyList<BookFingerprint> Aliases(BookFingerprint fingerprint) =>
        _aliases.TryGetValue(InMemoryScanCatalog.FingerprintKey(fingerprint), out var linked)
            ? linked.ToArray()
            : [];

    public void Link(BookFingerprint first, BookFingerprint second)
    {
        var firstKey = InMemoryScanCatalog.FingerprintKey(first);
        var secondKey = InMemoryScanCatalog.FingerprintKey(second);
        if (firstKey == secondKey) return;

        var changed = Add(firstKey, second) | Add(secondKey, first);
        if (changed) Persist();
    }

    private bool Add(string key, BookFingerprint value)
    {
        var valueKey = InMemoryScanCatalog.FingerprintKey(value);
        var list = _aliases.GetOrAdd(key, _ => []);
        lock (list)
        {
            if (list.Any(existing => InMemoryScanCatalog.FingerprintKey(existing) == valueKey))
            {
                return false;
            }
            list.Add(value);
            return true;
        }
    }

    private void Load()
    {
        if (string.IsNullOrWhiteSpace(_storagePath) || !File.Exists(_storagePath)) return;
        try
        {
            var state = JsonSerializer.Deserialize<Dictionary<string, List<BookFingerprint>>>(
                File.ReadAllText(_storagePath));
            if (state is null) return;
            foreach (var entry in state) _aliases[entry.Key] = entry.Value;
        }
        catch (Exception error) when (error is JsonException or IOException)
        {
            // Starting empty is correct here: the resolver rebuilds links on demand,
            // so a damaged cache must never stop the API from starting.
        }
    }

    private void Persist()
    {
        if (string.IsNullOrWhiteSpace(_storagePath)) return;
        lock (_persistenceLock)
        {
            var directory = Path.GetDirectoryName(_storagePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var snapshot = _aliases.ToDictionary(
                entry => entry.Key,
                entry =>
                {
                    lock (entry.Value) return entry.Value.ToList();
                });
            var temporary = _storagePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot));
            File.Move(temporary, _storagePath, true);
        }
    }
}
