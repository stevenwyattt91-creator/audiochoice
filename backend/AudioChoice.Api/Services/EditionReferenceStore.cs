using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

/// <summary>
/// Reports what in the database still points at an edition, without changing anything.
/// </summary>
/// <remarks>
/// Built for the question a delete needs answered first: nothing in
/// <c>audiobook_editions</c>'s referrers -- <c>user_library_books</c> above all, since that
/// is a real listener's own data -- has an <c>on delete cascade</c>. Every one of them uses
/// Postgres's default of refusing the delete instead, which is the safe behaviour, but it
/// means a plain delete of a duplicate edition fails as soon as any row anywhere still names
/// it, including a scan's own upload and job records, not only a listener's library.
/// </remarks>
public interface IEditionReferenceStore
{
    /// <summary>Null when the edition itself cannot be found.</summary>
    EditionReferenceCounts? CountReferences(BookFingerprint fingerprint);
}

/// <summary>
/// A count per referencing table. All zero is what a delete of the edition itself requires.
/// </summary>
public sealed record EditionReferenceCounts(
    int LibraryBooks,
    int ScanUploads,
    int ScanJobs,
    int ScanResults,
    int AuditAssignments,
    int ApprovedScanEvents,
    int AuditReviewMedia)
{
    /// <summary>
    /// Whether every referencing table is empty for this edition. <see cref="LibraryBooks"/>
    /// is a real listener's own data and the one that matters most, but it is not the only
    /// thing that blocks a delete: a scan's own upload, job and result rows reference the
    /// edition exactly the same way and with the same lack of a cascade, so an edition that
    /// was ever scanned will almost always still fail a delete on those alone.
    /// </summary>
    public bool HasNoReferences =>
        LibraryBooks == 0 && ScanUploads == 0 && ScanJobs == 0 && ScanResults == 0
        && AuditAssignments == 0 && ApprovedScanEvents == 0 && AuditReviewMedia == 0;
}

/// <summary>Used where no relational database is configured, where this question does not apply.</summary>
public sealed class UnavailableEditionReferenceStore : IEditionReferenceStore
{
    public EditionReferenceCounts? CountReferences(BookFingerprint fingerprint) => null;
}
