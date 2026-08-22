using System.Text.Json;
using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

public interface IUserDataStore
{
    IReadOnlyList<FilterProfile> ListProfiles(Guid userID);
    FilterProfile? SaveProfile(Guid userID, Guid? profileID, FilterProfileUpsertRequest request);
    bool DeleteProfile(Guid userID, Guid profileID);
    IReadOnlyList<BookNote> ListNotes(Guid userID, Guid? libraryBookID);
    BookNote? SaveNote(Guid userID, Guid? noteID, BookNoteUpsertRequest request);
    bool DeleteNote(Guid userID, Guid noteID);
    IReadOnlyList<LibraryCollection> ListCollections(Guid userID);
    LibraryCollection? SaveCollection(Guid userID, Guid? collectionID, CollectionUpsertRequest request);
    bool DeleteCollection(Guid userID, Guid collectionID);
    BookFilterSettings? GetBookFilterSettings(Guid userID, Guid libraryBookID);
    BookFilterSettings? SaveBookFilterSettings(Guid userID, Guid libraryBookID, BookFilterSettingsUpsertRequest request);
}

public sealed class FileUserDataStore(
    AudioChoiceDataPaths paths,
    IUserLibraryStore library) : IUserDataStore
{
    private readonly object _lock = new();
    private readonly string _path = paths.UserData;
    private State _state = Load(paths.UserData);

    public IReadOnlyList<FilterProfile> ListProfiles(Guid userID)
    {
        lock (_lock) return _state.Profiles.Where(x => x.UserID == userID)
            .Select(x => x.Value).OrderByDescending(x => x.IsActive)
            .ThenBy(x => x.Name).ToArray();
    }

    public FilterProfile? SaveProfile(
        Guid userID, Guid? profileID, FilterProfileUpsertRequest request)
    {
        var name = Clean(request.Name, 100);
        if (name is null || request.Rules.Count > 100 || request.CustomWords.Count > 500) return null;
        var rules = request.Rules.Select(CleanRule).Where(x => x is not null).Cast<FilterRule>().ToArray();
        var words = request.CustomWords.Select(x => Clean(x, 100)).Where(x => x is not null)
            .Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        lock (_lock)
        {
            var index = profileID is null ? -1 : _state.Profiles.FindIndex(x => x.UserID == userID && x.Value.ID == profileID);
            if (profileID is not null && index < 0) return null;
            var now = DateTimeOffset.UtcNow;
            if (request.IsActive)
            {
                for (var i = 0; i < _state.Profiles.Count; i++)
                    if (_state.Profiles[i].UserID == userID)
                        _state.Profiles[i] = _state.Profiles[i] with { Value = _state.Profiles[i].Value with { IsActive = false } };
            }
            var value = index >= 0
                ? _state.Profiles[index].Value with { Name = name, IsActive = request.IsActive, Rules = rules, CustomWords = words, UpdatedAt = now }
                : new FilterProfile(Guid.NewGuid(), name, request.IsActive, rules, words, now, now);
            if (index >= 0) _state.Profiles[index] = new(userID, value); else _state.Profiles.Add(new(userID, value));
            Persist();
            return value;
        }
    }

    public bool DeleteProfile(Guid userID, Guid profileID) => Delete(
        () => _state.Profiles.RemoveAll(x => x.UserID == userID && x.Value.ID == profileID) > 0);

    public IReadOnlyList<BookNote> ListNotes(Guid userID, Guid? libraryBookID)
    {
        lock (_lock) return _state.Notes.Where(x => x.UserID == userID &&
            (libraryBookID is null || x.Value.LibraryBookID == libraryBookID))
            .Select(x => x.Value).OrderByDescending(x => x.UpdatedAt).ToArray();
    }

    public BookNote? SaveNote(Guid userID, Guid? noteID, BookNoteUpsertRequest request)
    {
        var text = Clean(request.Text, 10_000);
        if (text is null || (request.PositionSeconds is double p && (!double.IsFinite(p) || p < 0)) ||
            !library.List(userID).Any(x => x.ID == request.LibraryBookID)) return null;
        lock (_lock)
        {
            var index = noteID is null ? -1 : _state.Notes.FindIndex(x => x.UserID == userID && x.Value.ID == noteID);
            if (noteID is not null && index < 0) return null;
            var now = DateTimeOffset.UtcNow;
            var value = index >= 0
                ? _state.Notes[index].Value with { LibraryBookID = request.LibraryBookID, PositionSeconds = request.PositionSeconds, Text = text, UpdatedAt = now }
                : new BookNote(Guid.NewGuid(), request.LibraryBookID, request.PositionSeconds, text, now, now);
            if (index >= 0) _state.Notes[index] = new(userID, value); else _state.Notes.Add(new(userID, value));
            Persist();
            return value;
        }
    }

    public bool DeleteNote(Guid userID, Guid noteID) => Delete(
        () => _state.Notes.RemoveAll(x => x.UserID == userID && x.Value.ID == noteID) > 0);

    public IReadOnlyList<LibraryCollection> ListCollections(Guid userID)
    {
        lock (_lock) return _state.Collections.Where(x => x.UserID == userID)
            .Select(x => x.Value).OrderBy(x => x.Name).ToArray();
    }

    public LibraryCollection? SaveCollection(
        Guid userID, Guid? collectionID, CollectionUpsertRequest request)
    {
        var name = Clean(request.Name, 100);
        if (name is null || request.LibraryBookIDs.Count > 1_000) return null;
        var owned = library.List(userID).Select(x => x.ID).ToHashSet();
        var books = request.LibraryBookIDs.Distinct().ToArray();
        if (books.Any(x => !owned.Contains(x))) return null;
        lock (_lock)
        {
            var index = collectionID is null ? -1 : _state.Collections.FindIndex(x => x.UserID == userID && x.Value.ID == collectionID);
            if (collectionID is not null && index < 0) return null;
            var now = DateTimeOffset.UtcNow;
            var value = index >= 0
                ? _state.Collections[index].Value with { Name = name, LibraryBookIDs = books, UpdatedAt = now }
                : new LibraryCollection(Guid.NewGuid(), name, books, now, now);
            if (index >= 0) _state.Collections[index] = new(userID, value); else _state.Collections.Add(new(userID, value));
            Persist();
            return value;
        }
    }

    public bool DeleteCollection(Guid userID, Guid collectionID) => Delete(
        () => _state.Collections.RemoveAll(x => x.UserID == userID && x.Value.ID == collectionID) > 0);

    public BookFilterSettings? GetBookFilterSettings(Guid userID, Guid libraryBookID)
    {
        if (!library.List(userID).Any(x => x.ID == libraryBookID)) return null;
        lock (_lock) return _state.BookFilterSettings
            .FirstOrDefault(x => x.UserID == userID && x.Value.LibraryBookID == libraryBookID)?.Value
            ?? new BookFilterSettings(libraryBookID, [], [], [], [], DateTimeOffset.UnixEpoch);
    }

    public BookFilterSettings? SaveBookFilterSettings(
        Guid userID, Guid libraryBookID, BookFilterSettingsUpsertRequest request)
    {
        if (!library.List(userID).Any(x => x.ID == libraryBookID) || !ValidSettings(request)) return null;
        var value = new BookFilterSettings(
            libraryBookID, request.DisabledCategoryIDs.Distinct().ToArray(),
            request.DisabledGroupIDs.Distinct().ToArray(), CleanKeys(request.DisabledEventKeys),
            CleanKeys(request.DisabledAggregateKeys), DateTimeOffset.UtcNow);
        lock (_lock)
        {
            var index = _state.BookFilterSettings.FindIndex(x => x.UserID == userID && x.Value.LibraryBookID == libraryBookID);
            if (index >= 0) _state.BookFilterSettings[index] = new(userID, value);
            else _state.BookFilterSettings.Add(new(userID, value));
            Persist();
            return value;
        }
    }

    private bool Delete(Func<bool> action)
    {
        lock (_lock) { var removed = action(); if (removed) Persist(); return removed; }
    }
    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_state));
        File.Move(temporary, _path, true);
    }
    private static State Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }
    private static FilterRule? CleanRule(FilterRule value)
    {
        var key = Clean(value.Key, 100); var action = Clean(value.Action, 30); var severity = Clean(value.Severity, 30);
        return key is null || action is null || severity is null ? null : new(key, value.Enabled, action, severity);
    }
    private static string? Clean(string? value, int maximum) => string.IsNullOrWhiteSpace(value)
        ? null : value.Trim()[..Math.Min(value.Trim().Length, maximum)];
    private static bool ValidSettings(BookFilterSettingsUpsertRequest value) =>
        value.DisabledCategoryIDs.Count <= 100 && value.DisabledGroupIDs.Count <= 200 &&
        value.DisabledEventKeys.Count <= 20_000 && value.DisabledAggregateKeys.Count <= 2_000;
    private static string[] CleanKeys(IEnumerable<string> values) => values
        .Select(x => Clean(x, 100)).Where(x => x is not null).Cast<string>()
        .Distinct(StringComparer.Ordinal).ToArray();

    public sealed class State
    {
        public List<ProfileRecord> Profiles { get; init; } = [];
        public List<NoteRecord> Notes { get; init; } = [];
        public List<CollectionRecord> Collections { get; init; } = [];
        public List<BookFilterSettingsRecord> BookFilterSettings { get; init; } = [];
    }
    public sealed record ProfileRecord(Guid UserID, FilterProfile Value);
    public sealed record NoteRecord(Guid UserID, BookNote Value);
    public sealed record CollectionRecord(Guid UserID, LibraryCollection Value);
    public sealed record BookFilterSettingsRecord(Guid UserID, BookFilterSettings Value);
}
