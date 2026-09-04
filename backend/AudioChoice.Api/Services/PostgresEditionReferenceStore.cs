#if POSTGRES
using AudioChoice.Api.Contracts;
using Npgsql;

namespace AudioChoice.Api.Services;

public sealed class PostgresEditionReferenceStore(NpgsqlDataSource dataSource) : IEditionReferenceStore
{
    public EditionReferenceCounts? CountReferences(BookFingerprint fingerprint)
    {
        using var connection = dataSource.OpenConnection();
        using var findEdition = new NpgsqlCommand("""
            select id from audiobook_editions
            where fingerprint_version = $1 and sha256 = $2 and file_size = $3;
            """, connection);
        findEdition.Parameters.AddWithValue(fingerprint.Version);
        findEdition.Parameters.AddWithValue(fingerprint.Sha256.ToLowerInvariant());
        findEdition.Parameters.AddWithValue(fingerprint.FileSize);
        var editionID = findEdition.ExecuteScalar() as Guid?;
        if (editionID is null) return null;

        // One round trip covering every table that names an edition and has no
        // on-delete-cascade back to it, so a caller sees the complete picture a delete
        // would be blocked by rather than discovering the second table on a failed attempt.
        using var command = new NpgsqlCommand("""
            select
                (select count(*) from user_library_books where edition_id = $1),
                (select count(*) from scan_uploads where edition_id = $1),
                (select count(*) from scan_jobs where edition_id = $1),
                (select count(*) from scan_results where edition_id = $1),
                (select count(*) from audit_assignments where edition_id = $1),
                (select count(*) from approved_scan_events where edition_id = $1),
                (select count(*) from audit_review_media where edition_id = $1);
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
            AuditReviewMedia: (int)(long)reader[6]);
    }
}
#endif
