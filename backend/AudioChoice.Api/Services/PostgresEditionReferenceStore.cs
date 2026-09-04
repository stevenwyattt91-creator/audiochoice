#if POSTGRES
using AudioChoice.Api.Contracts;
using Npgsql;

namespace AudioChoice.Api.Services;

public sealed class PostgresEditionReferenceStore(NpgsqlDataSource dataSource) : IEditionReferenceStore
{
    public EditionReferenceCounts? CountReferences(BookFingerprint fingerprint)
    {
        using var connection = dataSource.OpenConnection();
        var editionID = FindEditionID(connection, null, fingerprint);
        if (editionID is null) return null;

        // One round trip covering every table that names an edition and has no
        // on-delete-cascade back to it, so a caller sees the complete picture a delete
        // would be blocked by rather than discovering the second table on a failed attempt.
        // The last column looks past a bare count of audit_assignments, which says nothing
        // about whether real compensation is at stake: an assignment can exist and have been
        // abandoned with nothing paid and no auditor holding it.
        using var command = new NpgsqlCommand("""
            select
                (select count(*) from user_library_books where edition_id = $1),
                (select count(*) from scan_uploads where edition_id = $1),
                (select count(*) from scan_jobs where edition_id = $1),
                (select count(*) from scan_results where edition_id = $1),
                (select count(*) from audit_assignments where edition_id = $1),
                (select count(*) from approved_scan_events where edition_id = $1),
                (select count(*) from audit_review_media where edition_id = $1),
                exists(
                    select 1 from audit_assignments
                    where edition_id = $1
                      and (payment_status = 'paid' or status in ('in_progress', 'completed', 'needs_review')));
            """, connection);
        command.Parameters.AddWithValue(editionID.Value);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new EditionReferenceCounts(
            LibraryBooks: (int)(long)reader[0],
            ScanUploads: (int)(long)reader[1],
            ScanJobs: (int)(long)reader[2],
            ScanResults: (int)(long)reader[3],
            AuditAssignments: (int)(long)reader[4],
            ApprovedScanEvents: (int)(long)reader[5],
            AuditReviewMedia: (int)(long)reader[6],
            HasPaidOrActiveAuditWork: (bool)reader[7]);
    }

    public IReadOnlyList<EditionAuditAssignmentSummary>? ListAuditAssignments(BookFingerprint fingerprint)
    {
        using var connection = dataSource.OpenConnection();
        var editionID = FindEditionID(connection, null, fingerprint);
        if (editionID is null) return null;

        // Includes which scan_results row and scanner version the assignment actually points
        // at, and whether that is the edition's current latest result. scan_result_id is set
        // once, at creation, and never moves -- an assignment created before a later scan (a
        // rescan, or a result-copy inserting a fresh scanner_version row for the edition)
        // silently keeps pointing an auditor at what they were originally handed rather than
        // whatever /v1/admin/editions/result now reports as latest for the edition.
        using var command = new NpgsqlCommand("""
            select a.id, a.status, a.auditor_id, a.compensation_amount, a.payment_status,
                   a.payment_date, a.scan_result_id, r.scanner_version, r.scanned_at,
                   r.id = (select id from scan_results where edition_id = $1 order by scanned_at desc limit 1)
            from audit_assignments a
            join scan_results r on r.id = a.scan_result_id
            where a.edition_id = $1
            order by a.created_at;
            """, connection);
        command.Parameters.AddWithValue(editionID.Value);
        using var reader = command.ExecuteReader();
        var assignments = new List<EditionAuditAssignmentSummary>();
        while (reader.Read())
        {
            assignments.Add(new EditionAuditAssignmentSummary(
                AssignmentID: reader.GetGuid(0),
                Status: reader.GetString(1),
                AuditorID: reader.IsDBNull(2) ? null : reader.GetGuid(2),
                CompensationAmount: reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                PaymentStatus: reader.GetString(4),
                PaymentDate: reader.IsDBNull(5) ? null : DateOnly.FromDateTime(reader.GetDateTime(5)),
                ScanResultID: reader.GetGuid(6),
                ScannerVersion: reader.GetString(7),
                ScannedAt: reader.GetDateTime(8),
                PointsAtLatestResult: reader.GetBoolean(9)));
        }
        return assignments;
    }

    public EditionRepointResult? RepointLibraryBooks(BookFingerprint source, BookFingerprint destination)
    {
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var sourceID = FindEditionID(connection, transaction, source);
        var destinationID = FindEditionID(connection, transaction, destination);
        if (sourceID is null || destinationID is null) return null;
        if (sourceID == destinationID) return new EditionRepointResult(0, 0);

        // A row already at the destination for the same listener is left alone rather than
        // merged. Moving it would collide with user_library_books' own (user_id, edition_id)
        // uniqueness, and even if it did not, merging is a choice between that listener's two
        // playback positions and favorite/finished flags -- their own data -- that is not this
        // endpoint's to make.
        using var repoint = new NpgsqlCommand("""
            update user_library_books set edition_id = $1, updated_at = now()
            where edition_id = $2
              and not exists (
                  select 1 from user_library_books existing
                  where existing.user_id = user_library_books.user_id
                    and existing.edition_id = $1);
            """, connection, transaction);
        repoint.Parameters.AddWithValue(destinationID.Value);
        repoint.Parameters.AddWithValue(sourceID.Value);
        var repointed = repoint.ExecuteNonQuery();

        using var remaining = new NpgsqlCommand(
            "select count(*) from user_library_books where edition_id = $1;", connection, transaction);
        remaining.Parameters.AddWithValue(sourceID.Value);
        var skipped = (int)(long)remaining.ExecuteScalar()!;

        transaction.Commit();
        return new EditionRepointResult(repointed, skipped);
    }

    public EditionAuditRetargetResult? RetargetAvailableAuditAssignments(BookFingerprint fingerprint)
    {
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var editionID = FindEditionID(connection, transaction, fingerprint);
        if (editionID is null) return null;

        using var latest = new NpgsqlCommand(
            "select id from scan_results where edition_id = $1 order by scanned_at desc limit 1;",
            connection, transaction);
        latest.Parameters.AddWithValue(editionID.Value);
        var latestResultID = latest.ExecuteScalar() as Guid?;
        if (latestResultID is null) return new EditionAuditRetargetResult(0, 0);

        // status = 'available' and auditor_id is null is the same pair Claim() itself checks
        // before letting an auditor take an assignment, so this only ever moves an assignment
        // no one has started -- one already claimed, completed or under review is skipped,
        // not retargeted, because its audit_decisions rows name specific scan_event ids from
        // the result it was created against and moving the assignment would orphan them.
        using var retarget = new NpgsqlCommand("""
            update audit_assignments set scan_result_id = $1, updated_at = now()
            where edition_id = $2 and status = 'available' and auditor_id is null
              and scan_result_id != $1;
            """, connection, transaction);
        retarget.Parameters.AddWithValue(latestResultID.Value);
        retarget.Parameters.AddWithValue(editionID.Value);
        var retargeted = retarget.ExecuteNonQuery();

        using var skipped = new NpgsqlCommand("""
            select count(*) from audit_assignments
            where edition_id = $1 and scan_result_id != $2
              and not (status = 'available' and auditor_id is null);
            """, connection, transaction);
        skipped.Parameters.AddWithValue(editionID.Value);
        skipped.Parameters.AddWithValue(latestResultID.Value);
        var skippedCount = (int)(long)skipped.ExecuteScalar()!;

        transaction.Commit();
        return new EditionAuditRetargetResult(retargeted, skippedCount);
    }

    public EditionDeleteResult DeleteEdition(BookFingerprint fingerprint, bool discardActiveAuditWork = false)
    {
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();

        var editionID = FindEditionID(connection, transaction, fingerprint);
        if (editionID is null) return new EditionDeleteResult(EditionDeleteOutcome.NotFound);

        // Re-read inside this transaction rather than trust whatever CountReferences returned
        // to the caller a moment earlier: a listener could have added this edition to their
        // library, or an auditor could have claimed or been paid for an assignment on it, in
        // the time between that check and this call. Never overridden by
        // discardActiveAuditWork -- a listener's own library data has no override, on either
        // endpoint.
        using var libraryCheck = new NpgsqlCommand(
            "select count(*) from user_library_books where edition_id = $1;", connection, transaction);
        libraryCheck.Parameters.AddWithValue(editionID.Value);
        if ((long)libraryCheck.ExecuteScalar()! > 0)
        {
            return new EditionDeleteResult(EditionDeleteOutcome.RefusedLibraryBooksPresent);
        }

        using var auditCheck = new NpgsqlCommand("""
            select exists(
                select 1 from audit_assignments
                where edition_id = $1
                  and (payment_status = 'paid' or status in ('in_progress', 'completed', 'needs_review')));
            """, connection, transaction);
        auditCheck.Parameters.AddWithValue(editionID.Value);
        var hasPaidOrActiveAuditWork = (bool)auditCheck.ExecuteScalar()!;
        if (hasPaidOrActiveAuditWork && !discardActiveAuditWork)
        {
            return new EditionDeleteResult(EditionDeleteOutcome.RefusedPaidOrActiveAuditWork);
        }

        // Deleted in dependency order rather than left to cascades, because most of these
        // relationships deliberately have none -- see the remarks on IEditionReferenceStore.
        // approved_scan_events and audit_assignments come first: both name a scan_event_id or
        // scan_result_id without a cascade of their own, so either would block the scan_results
        // delete below if left in place. scan_jobs before scan_uploads for the same reason, in
        // the other direction: scan_jobs.upload_id names scan_uploads, so the upload row must
        // outlive its job, not the other way around.
        void Execute(string sql)
        {
            using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue(editionID.Value);
            command.ExecuteNonQuery();
        }

        Execute("delete from approved_scan_events where edition_id = $1;");
        // Cascades to audit_decisions, audit_review_sources and audit_review_clips.
        Execute("delete from audit_assignments where edition_id = $1;");
        Execute("delete from audit_review_media where edition_id = $1;");
        // Cascades to scan_events.
        Execute("delete from scan_results where edition_id = $1;");
        // Cascades to scan_job_subscribers.
        Execute("delete from scan_jobs where edition_id = $1;");
        Execute("delete from scan_uploads where edition_id = $1;");
        Execute("delete from private_transcripts where edition_id = $1;");
        Execute("delete from audiobook_editions where id = $1;");

        transaction.Commit();
        return new EditionDeleteResult(
            hasPaidOrActiveAuditWork
                ? EditionDeleteOutcome.DeletedDiscardingAuditWork
                : EditionDeleteOutcome.Deleted);
    }

    private static Guid? FindEditionID(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, BookFingerprint fingerprint)
    {
        using var command = new NpgsqlCommand("""
            select id from audiobook_editions
            where fingerprint_version = $1 and sha256 = $2 and file_size = $3;
            """, connection, transaction);
        command.Parameters.AddWithValue(fingerprint.Version);
        command.Parameters.AddWithValue(fingerprint.Sha256.ToLowerInvariant());
        command.Parameters.AddWithValue(fingerprint.FileSize);
        return command.ExecuteScalar() as Guid?;
    }
}
#endif
