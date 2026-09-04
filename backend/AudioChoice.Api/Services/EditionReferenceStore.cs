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

    /// <summary>
    /// Points every listener's library row at <paramref name="destination"/> instead of
    /// <paramref name="source"/>, so a duplicate edition can be retired without breaking
    /// the library of anyone who happened to import that exact file.
    /// </summary>
    /// <remarks>
    /// A row is left alone rather than merged when the same listener already has one at
    /// the destination, because merging would silently pick a winner between two accounts
    /// of that listener's own data -- playback position, favorite, finished -- which is
    /// not a decision to make on their behalf without asking. Null when either fingerprint
    /// cannot be found.
    /// </remarks>
    EditionRepointResult? RepointLibraryBooks(BookFingerprint source, BookFingerprint destination);

    /// <summary>
    /// The detail behind <see cref="EditionReferenceCounts.HasPaidOrActiveAuditWork"/>: every
    /// audit assignment on the edition, with enough of its own fields to judge by hand whether
    /// retiring the edition would touch real compensation. Null when the edition cannot be found.
    /// </summary>
    IReadOnlyList<EditionAuditAssignmentSummary>? ListAuditAssignments(BookFingerprint fingerprint);

    /// <summary>
    /// Permanently removes an edition and everything that exists only to describe it: its
    /// scan uploads, jobs, results and events, and any audit assignment and decision made
    /// against it.
    /// </summary>
    /// <remarks>
    /// Refuses rather than trusts the caller's own prior check, for the same reason
    /// <c>/v1/admin/editions/result-copy</c> re-derives its own match instead of trusting the
    /// caller's claim: a listener's own library row and any paid or actively claimed audit
    /// work are re-read inside the same transaction as the delete, not assumed still true from
    /// whatever <see cref="CountReferences"/> returned a moment earlier to whoever is calling
    /// this. A library row is never deleted by this or anything it touches; if one still
    /// exists this refuses outright rather than removing an edition out from under it.
    ///
    /// <paramref name="discardActiveAuditWork"/> is a second, separate override from the
    /// caller's own irreversibility confirmation: it exists for the one case an operator may
    /// knowingly choose to discard a real auditor's in-progress, uncompensated claim on a
    /// fingerprint being retired as a duplicate, and defaults to false so the ordinary path
    /// still refuses outright. Never bypasses the library-row check -- that one has no
    /// override, on either endpoint, because it is a listener's own data rather than
    /// compensable work an operator can choose to write off.
    /// </remarks>
    EditionDeleteResult DeleteEdition(BookFingerprint fingerprint, bool discardActiveAuditWork = false);
}

public enum EditionDeleteOutcome
{
    Deleted,
    /// <summary>Deleted with a real auditor's active or unpaid claim knowingly discarded.</summary>
    DeletedDiscardingAuditWork,
    NotFound,
    RefusedLibraryBooksPresent,
    RefusedPaidOrActiveAuditWork
}

public sealed record EditionDeleteResult(EditionDeleteOutcome Outcome);

/// <param name="Repointed">Rows moved to the destination edition.</param>
/// <param name="SkippedForExistingRow">
/// Rows left on the source edition because that listener already has a row at the
/// destination.
/// </param>
public sealed record EditionRepointResult(int Repointed, int SkippedForExistingRow);

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
    int AuditReviewMedia,
    /// <summary>
    /// Whether any of this edition's audit assignments has ever been paid, or is currently
    /// claimed by an auditor. A count alone does not say this: an edition scanned only for
    /// this investigation could carry an assignment that was created and then abandoned,
    /// indistinguishable from one with real compensation owed on it without asking the
    /// database this directly.
    /// </summary>
    bool HasPaidOrActiveAuditWork)
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
    public EditionRepointResult? RepointLibraryBooks(BookFingerprint source, BookFingerprint destination) => null;
    public IReadOnlyList<EditionAuditAssignmentSummary>? ListAuditAssignments(BookFingerprint fingerprint) => null;
    public EditionDeleteResult DeleteEdition(BookFingerprint fingerprint, bool discardActiveAuditWork = false) =>
        new(EditionDeleteOutcome.NotFound);
}

/// <summary>
/// Every audit assignment on an edition, so an operator can read what compensation or
/// active claim exists before deciding a duplicate is safe to retire.
/// </summary>
public sealed record EditionAuditAssignmentSummary(
    Guid AssignmentID,
    string Status,
    Guid? AuditorID,
    decimal? CompensationAmount,
    string PaymentStatus,
    DateOnly? PaymentDate,
    /// <summary>
    /// The scan_results row this assignment was created against. Fixed at creation and never
    /// updated -- an assignment can silently outlive the result it points at.
    /// </summary>
    Guid ScanResultID,
    string ScannerVersion,
    DateTimeOffset ScannedAt,
    /// <summary>
    /// False means an auditor working this assignment is looking at events from an older
    /// scan than the edition's current latest, most likely because the edition was rescanned
    /// or had a result copied onto it after this assignment was created.
    /// </summary>
    bool PointsAtLatestResult);
