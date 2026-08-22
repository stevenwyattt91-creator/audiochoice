using System.Text.Json;
using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

public interface IUserLibraryStore
{
    IReadOnlyList<LibraryBook> List(Guid userID);
    LibraryBook Upsert(Guid userID, LibraryBookUpsertRequest request);
    LibraryBook? UpdateProgress(Guid userID, Guid bookID, PlaybackProgressRequest request);
    LibraryBook? UpdateFavorite(Guid userID, Guid bookID, bool isFavorite);
    bool DeleteBook(Guid userID, Guid bookID);
    IReadOnlyList<LibraryBookmark>? ListBookmarks(Guid userID, Guid bookID);
    LibraryBookmark? AddBookmark(Guid userID, Guid bookID, BookmarkCreateRequest request);
    bool DeleteBookmark(Guid userID, Guid bookmarkID);
}

/// Local-development adapter for the same contracts used by the production store.
public sealed class FileUserLibraryStore : IUserLibraryStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private LibraryState _state;

    public FileUserLibraryStore(string path)
    {
        _path = path;
        _state = Load(path);
    }

    public IReadOnlyList<LibraryBook> List(Guid userID)
    {
        lock (_lock)
        {
            return _state.Books.Where(value => value.UserID == userID)
                .OrderByDescending(value => value.Book.UpdatedAt)
                .Select(value => value.Book).ToArray();
        }
    }

    public LibraryBook Upsert(Guid userID, LibraryBookUpsertRequest request)
    {
        lock (_lock)
        {
            var key = InMemoryScanCatalog.FingerprintKey(request.Fingerprint);
            var index = _state.Books.FindIndex(value =>
                value.UserID == userID && value.FingerprintKey == key);
            var now = DateTimeOffset.UtcNow;
            if (index >= 0)
            {
                var record = _state.Books[index];
                var updated = record.Book with
                {
                    Fingerprint = request.Fingerprint,
                    Title = Required(request.Title, record.Book.Title),
                    Author = Optional(request.Author),
                    Narrator = Optional(request.Narrator),
                    CoverImageURL = Optional(request.CoverImageURL) ?? record.Book.CoverImageURL,
                    UpdatedAt = now
                };
                _state.Books[index] = record with { Book = updated };
                Persist();
                return updated;
            }

            var book = new LibraryBook(
                Guid.NewGuid(), request.Fingerprint,
                Required(request.Title, "Untitled Audiobook"),
                Optional(request.Author), Optional(request.Narrator),
                Optional(request.CoverImageURL), 0, false, false, now, now);
            _state.Books.Add(new UserBookRecord(userID, key, book));
            Persist();
            return book;
        }
    }

    public LibraryBook? UpdateProgress(
        Guid userID, Guid bookID, PlaybackProgressRequest request)
    {
        if (!double.IsFinite(request.PositionSeconds) || request.PositionSeconds < 0) return null;
        return Update(userID, bookID, value => value with
        {
            PlaybackPositionSeconds = request.PositionSeconds,
            IsFinished = request.IsFinished,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    public LibraryBook? UpdateFavorite(Guid userID, Guid bookID, bool isFavorite) =>
        Update(userID, bookID, value => value with
        {
            IsFavorite = isFavorite,
            UpdatedAt = DateTimeOffset.UtcNow
        });

    public IReadOnlyList<LibraryBookmark>? ListBookmarks(Guid userID, Guid bookID)
    {
        lock (_lock)
        {
            if (!Owns(userID, bookID)) return null;
            return _state.Bookmarks
                .Where(value => value.UserID == userID && value.Bookmark.LibraryBookID == bookID)
                .OrderBy(value => value.Bookmark.PositionSeconds)
                .Select(value => value.Bookmark).ToArray();
        }
    }

    public LibraryBookmark? AddBookmark(
        Guid userID, Guid bookID, BookmarkCreateRequest request)
    {
        if (!double.IsFinite(request.PositionSeconds) || request.PositionSeconds < 0) return null;
        lock (_lock)
        {
            if (!Owns(userID, bookID)) return null;
            var now = DateTimeOffset.UtcNow;
            var bookmark = new LibraryBookmark(
                Guid.NewGuid(), bookID, request.PositionSeconds,
                Optional(request.Title), Optional(request.Note, 2_000), now, now);
            _state.Bookmarks.Add(new UserBookmarkRecord(userID, bookmark));
            Persist();
            return bookmark;
        }
    }

    public bool DeleteBookmark(Guid userID, Guid bookmarkID)
    {
        lock (_lock)
        {
            var removed = _state.Bookmarks.RemoveAll(value =>
                value.UserID == userID && value.Bookmark.ID == bookmarkID) > 0;
            if (removed) Persist();
            return removed;
        }
    }

    public bool DeleteBook(Guid userID, Guid bookID)
    {
        lock (_lock)
        {
            var removed = _state.Books.RemoveAll(value =>
                value.UserID == userID && value.Book.ID == bookID) > 0;
            if (!removed) return false;
            _state.Bookmarks.RemoveAll(value =>
                value.UserID == userID && value.Bookmark.LibraryBookID == bookID);
            Persist();
            return true;
        }
    }

    private LibraryBook? Update(Guid userID, Guid bookID, Func<LibraryBook, LibraryBook> update)
    {
        lock (_lock)
        {
            var index = _state.Books.FindIndex(value =>
                value.UserID == userID && value.Book.ID == bookID);
            if (index < 0) return null;
            var record = _state.Books[index];
            var updated = update(record.Book);
            _state.Books[index] = record with { Book = updated };
            Persist();
            return updated;
        }
    }

    private bool Owns(Guid userID, Guid bookID) =>
        _state.Books.Any(value => value.UserID == userID && value.Book.ID == bookID);

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_state));
        File.Move(temporary, _path, true);
    }

    private static LibraryState Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<LibraryState>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch (JsonException) { return new(); }
    }

    private static string Required(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim()[..Math.Min(value.Trim().Length, 300)];
    private static string? Optional(string? value, int maximum = 500) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maximum)];

    public sealed class LibraryState
    {
        public List<UserBookRecord> Books { get; init; } = [];
        public List<UserBookmarkRecord> Bookmarks { get; init; } = [];
    }
    public sealed record UserBookRecord(Guid UserID, string FingerprintKey, LibraryBook Book);
    public sealed record UserBookmarkRecord(Guid UserID, LibraryBookmark Bookmark);
}
