namespace AudioChoice.Api.Contracts;

public sealed record LibraryBookUpsertRequest(
    BookFingerprint Fingerprint,
    string Title,
    string? Author,
    string? Narrator,
    string? CoverImageURL,
    string? CoverImageBase64 = null,
    string? CoverImageContentType = null);

public sealed record LibraryBook(
    Guid ID,
    BookFingerprint Fingerprint,
    string Title,
    string? Author,
    string? Narrator,
    string? CoverImageURL,
    double PlaybackPositionSeconds,
    bool IsFinished,
    bool IsFavorite,
    DateTimeOffset AddedAt,
    DateTimeOffset UpdatedAt);

public sealed record PlaybackProgressRequest(double PositionSeconds, bool IsFinished);
public sealed record FavoriteRequest(bool IsFavorite);

public sealed record BookmarkCreateRequest(
    double PositionSeconds,
    string? Title,
    string? Note);

public sealed record LibraryBookmark(
    Guid ID,
    Guid LibraryBookID,
    double PositionSeconds,
    string? Title,
    string? Note,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
