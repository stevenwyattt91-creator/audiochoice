namespace AudioChoice.Api.Contracts;

public sealed record LibraryBookUpsertRequest(
    BookFingerprint Fingerprint,
    string Title,
    string? Author,
    string? Narrator,
    string? CoverImageURL,
    string? CoverImageBase64 = null,
    string? CoverImageContentType = null,
    /// <summary>
    /// The fingerprint of the file actually on the listener's device, when it
    /// differs from <paramref name="Fingerprint"/>.
    /// </summary>
    /// <remarks>
    /// Clients adopt a canonical edition's fingerprint so that a converted file does
    /// not create a duplicate library row. That leaves the transcript stored under
    /// the *source* file's fingerprint and unreachable from the library row.
    /// Reporting it here links the two at the moment the divergence is created,
    /// rather than leaving it to be rediscovered later.
    /// </remarks>
    BookFingerprint? SourceFingerprint = null,
    /// <summary>
    /// Identity evidence read from the file's own tags. Only the client can see this,
    /// and it is what lets a converted copy be recognised as the same recording.
    /// </summary>
    EditionSignature? Signature = null);

/// <summary>
/// Reports identity evidence for an audiobook already in the caller's library.
/// </summary>
/// <remarks>
/// Signatures are gathered at import, so books added before that existed carry none
/// and get no benefit from edition matching. This lets a client supply the evidence
/// afterwards without rewriting the library row's title, author or artwork.
/// </remarks>
/// <summary>
/// Corrects the display details of a book in a listener's own library.
/// </summary>
/// <remarks>
/// Needed because a file without tags leaves AudioChoice guessing a title from the
/// filename, and until now there was no way to put that right.
///
/// Display only. These values deliberately do not feed edition identification: the
/// matching rules work from the file's own metadata, and letting typed-in text steer
/// which recording a book is taken to be would undermine that.
/// </remarks>
public sealed record LibraryBookDetailsRequest(
    string Title,
    string? Author = null,
    string? Narrator = null);

public sealed record EditionSignatureReportRequest(
    BookFingerprint Fingerprint,
    EditionSignature Signature,
    BookFingerprint? SourceFingerprint = null);

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
