#if POSTGRES
using AudioChoice.Api.Contracts;
using Npgsql;

namespace AudioChoice.Api.Services;

public sealed class PostgresFilterReportStore(NpgsqlDataSource dataSource) : IFilterReportStore
{
    public FilterReport? Record(Guid userID, FilterReportRequest request)
    {
        var report = FilterReports.Validate(userID, request);
        if (report is null) return null;

        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            insert into filter_reports(
                id, user_id, fingerprint_version, sha256, file_size, kind,
                position_seconds, window_seconds, scanner_version, scan_event_id,
                category_id, reported_at)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12);
            """, connection);
        command.Parameters.AddWithValue(report.ID);
        command.Parameters.AddWithValue(report.AccountID);
        command.Parameters.AddWithValue(report.Fingerprint.Version);
        command.Parameters.AddWithValue(report.Fingerprint.Sha256.ToLowerInvariant());
        command.Parameters.AddWithValue(report.Fingerprint.FileSize);
        command.Parameters.AddWithValue(report.Kind.ToString());
        command.Parameters.AddWithValue(report.PositionSeconds);
        command.Parameters.AddWithValue(report.WindowSeconds);
        AddNullable(command, report.ScannerVersion);
        AddNullable(command, report.ScanEventID);
        AddNullable(command, report.CategoryID);
        command.Parameters.AddWithValue(report.ReportedAt);

        // A report about a book the account does not own is refused by the foreign key on
        // users only, not on editions, so an unmatched edition still records.
        try
        {
            return command.ExecuteNonQuery() > 0 ? report : null;
        }
        catch (PostgresException)
        {
            return null;
        }
    }

    public IReadOnlyList<FilterReport> List(int limit = 200, BookFingerprint? fingerprint = null)
    {
        var filtered = fingerprint is not null;
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand($"""
            select id, user_id, fingerprint_version, sha256, file_size, kind,
                   position_seconds, window_seconds, scanner_version, scan_event_id,
                   category_id, reported_at
            from filter_reports
            {(filtered ? "where fingerprint_version = $2 and lower(sha256) = $3 and file_size = $4" : "")}
            order by reported_at desc
            limit $1;
            """, connection);
        command.Parameters.AddWithValue(Math.Clamp(limit, 1, 1000));
        if (filtered)
        {
            command.Parameters.AddWithValue(fingerprint!.Version);
            command.Parameters.AddWithValue(fingerprint.Sha256.ToLowerInvariant());
            command.Parameters.AddWithValue(fingerprint.FileSize);
        }

        using var reader = command.ExecuteReader();
        var values = new List<FilterReport>();
        while (reader.Read())
        {
            values.Add(new FilterReport(
                reader.GetGuid(0),
                reader.GetGuid(1),
                // Only the identifying three columns are stored, so the rest of the
                // fingerprint is left unset rather than invented. Triage matches editions
                // on these, and guessing a title here would make a report look like
                // evidence about metadata it never carried.
                new BookFingerprint(
                    reader.GetInt32(2), reader.GetString(3).Trim(), reader.GetInt64(4),
                    null, "", null, null, null, null, null, null, null),
                Enum.TryParse<FilterReportKind>(reader.GetString(5), out var kind)
                    ? kind
                    : FilterReportKind.MissedContent,
                reader.GetDouble(6),
                reader.GetDouble(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetGuid(9),
                reader.IsDBNull(10) ? null : reader.GetGuid(10),
                reader.GetFieldValue<DateTimeOffset>(11)));
        }
        return values;
    }

    private static void AddNullable(NpgsqlCommand command, string? value) =>
        command.Parameters.AddWithValue(value is null ? DBNull.Value : value);

    private static void AddNullable(NpgsqlCommand command, Guid? value) =>
        command.Parameters.AddWithValue(value is null ? DBNull.Value : value.Value);
}
#endif
