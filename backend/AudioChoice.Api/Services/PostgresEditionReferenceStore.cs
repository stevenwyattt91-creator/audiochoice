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

        using var command = new NpgsqlCommand("""
            select id, status, auditor_id, compensation_amount, payment_status, payment_date
            from audit_assignments where edition_id = $1 order by created_at;
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
                PaymentDate: reader.IsDBNull(5) ? null : DateOnly.FromDateTime(reader.GetDateTime(5))));
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
