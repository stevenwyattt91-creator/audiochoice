#if POSTGRES
using AudioChoice.Api.Contracts;
using Npgsql;

namespace AudioChoice.Api.Services;

/// <summary>
/// Records text-derived filter events in <c>narration_text_scans</c> and
/// <c>narration_text_scan_events</c>.
/// </summary>
/// <remarks>
/// Writes to those two tables and nothing else. In particular it creates no
/// <c>scan_results</c> row, which is what the Explore catalogue is assembled from, so a book
/// a listener supplied cannot become a public catalogue entry.
///
/// Offsets are stored in columns named for characters rather than seconds. The client carries
/// them in a <c>ScanEvent</c>'s time fields, because that is what lets the entire existing
/// filter stack work unchanged, but there is no reason for the database to inherit that
/// ambiguity -- and a check constraint on the range means an inverted one cannot be written
/// at all.
/// </remarks>
public sealed class PostgresNarrationTextScanStore(NpgsqlDataSource dataSource)
    : INarrationTextScanStore
{
    public NarrationTextScan? Load(BookFingerprint fingerprint, string scannerVersion)
    {
        using var connection = dataSource.OpenConnection();
        using var header = new NpgsqlCommand("""
            select id, scanned_at, scanner_version, taxonomy_version,
                   book_text_characters, language
            from narration_text_scans
            where fingerprint_version = $1 and lower(sha256) = $2
              and file_size = $3 and scanner_version = $4;
            """, connection);
        header.Parameters.AddWithValue(fingerprint.Version);
        header.Parameters.AddWithValue(fingerprint.Sha256.ToLowerInvariant());
        header.Parameters.AddWithValue(fingerprint.FileSize);
        header.Parameters.AddWithValue(scannerVersion);

        Guid scanID;
        DateTimeOffset scannedAt;
        string storedScannerVersion;
        string taxonomyVersion;
        int bookTextCharacters;
        string? language;
        using (var reader = header.ExecuteReader())
        {
            if (!reader.Read()) return null;
            scanID = reader.GetGuid(0);
            scannedAt = reader.GetFieldValue<DateTimeOffset>(1);
            storedScannerVersion = reader.GetString(2);
            taxonomyVersion = reader.GetString(3);
            bookTextCharacters = reader.GetInt32(4);
            language = reader.IsDBNull(5) ? null : reader.GetString(5);
        }

        var events = new List<ScanEvent>();
        using var eventCommand = new NpgsqlCommand("""
            select id, start_character, end_character, category_id, group_id, event_id,
                   confidence, stable_key, safe_description, aggregate_key, aggregate_display
            from narration_text_scan_events
            where scan_id = $1
            order by start_character;
            """, connection);
        eventCommand.Parameters.AddWithValue(scanID);
        using (var reader = eventCommand.ExecuteReader())
        {
            while (reader.Read())
            {
                events.Add(new ScanEvent(
                    reader.GetGuid(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2),
                    reader.GetGuid(3),
                    reader.GetGuid(4),
                    reader.GetGuid(5),
                    reader.GetDouble(6),
                    reader.GetString(7),
                    reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10)));
            }
        }

        return new NarrationTextScan(
            events, scannedAt, storedScannerVersion, taxonomyVersion,
            bookTextCharacters, language);
    }

    public void Save(BookFingerprint fingerprint, NarrationTextScan scan)
    {
        if (!NarrationTextScans.IsStorable(scan)) return;

        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        try
        {
            // Re-scanning the same book at the same scanner version replaces rather than
            // accumulates. The events cascade from the header row's unique key.
            using (var delete = new NpgsqlCommand("""
                delete from narration_text_scans
                where fingerprint_version = $1 and lower(sha256) = $2
                  and file_size = $3 and scanner_version = $4;
                """, connection, transaction))
            {
                delete.Parameters.AddWithValue(fingerprint.Version);
                delete.Parameters.AddWithValue(fingerprint.Sha256.ToLowerInvariant());
                delete.Parameters.AddWithValue(fingerprint.FileSize);
                delete.Parameters.AddWithValue(scan.ScannerVersion);
                delete.ExecuteNonQuery();
            }

            var scanID = Guid.NewGuid();
            using (var header = new NpgsqlCommand("""
                insert into narration_text_scans(
                    id, fingerprint_version, sha256, file_size, language, scanner_version,
                    taxonomy_version, book_text_characters, scanned_at)
                values ($1, $2, $3, $4, $5, $6, $7, $8, $9);
                """, connection, transaction))
            {
                header.Parameters.AddWithValue(scanID);
                header.Parameters.AddWithValue(fingerprint.Version);
                header.Parameters.AddWithValue(fingerprint.Sha256.ToLowerInvariant());
                header.Parameters.AddWithValue(fingerprint.FileSize);
                AddNullable(header, scan.Language);
                header.Parameters.AddWithValue(scan.ScannerVersion);
                header.Parameters.AddWithValue(scan.TaxonomyVersion);
                header.Parameters.AddWithValue(scan.BookTextCharacters);
                header.Parameters.AddWithValue(scan.ScanDate);
                header.ExecuteNonQuery();
            }

            foreach (var item in scan.Events)
            {
                using var command = new NpgsqlCommand("""
                    insert into narration_text_scan_events(
                        id, scan_id, start_character, end_character, category_id, group_id,
                        event_id, confidence, stable_key, safe_description, aggregate_key,
                        aggregate_display)
                    values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12);
                    """, connection, transaction);
                command.Parameters.AddWithValue(item.Id == Guid.Empty ? Guid.NewGuid() : item.Id);
                command.Parameters.AddWithValue(scanID);
                command.Parameters.AddWithValue((int)item.StartTime);
                command.Parameters.AddWithValue((int)item.EndTime);
                command.Parameters.AddWithValue(item.CategoryID);
                command.Parameters.AddWithValue(item.GroupID);
                command.Parameters.AddWithValue(item.EventID);
                command.Parameters.AddWithValue(item.Confidence);
                command.Parameters.AddWithValue(item.StableKey);
                command.Parameters.AddWithValue(item.SafeDescription);
                AddNullable(command, item.AggregateKey);
                AddNullable(command, item.AggregateDisplay);
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        catch (PostgresException)
        {
            transaction.Rollback();
            // A scan is a cache. Failing to record one costs a re-scan of this book, and
            // must not fail the request that produced perfectly usable events.
        }
    }

    private static void AddNullable(NpgsqlCommand command, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            command.Parameters.AddWithValue(DBNull.Value);
            return;
        }
        command.Parameters.AddWithValue(value);
    }
}
#endif
