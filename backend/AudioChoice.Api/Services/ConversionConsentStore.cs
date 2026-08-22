using System.Text.Json;
using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

public interface IConversionConsentStore
{
    ConversionConsentRecord Record(AuthUser user, ConversionConsentRequest request);
    IReadOnlyList<ConversionConsentRecord> Search(string? query, int limit);
    ConversionConsentRecord? Find(Guid id);
}

public sealed class FileConversionConsentStore : IConversionConsentStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private List<ConversionConsentRecord> _records;

    public FileConversionConsentStore(string path)
    {
        _path = path;
        _records = File.Exists(path)
            ? JsonSerializer.Deserialize<List<ConversionConsentRecord>>(File.ReadAllText(path)) ?? []
            : [];
    }

    public ConversionConsentRecord Record(AuthUser user, ConversionConsentRequest request)
    {
        Validate(request);
        request = request with
        {
            Fingerprint = request.Fingerprint.Version <= 0
                ? request.Fingerprint with { Version = 1 }
                : request.Fingerprint
        };
        lock (_lock)
        {
            var existing = _records.FirstOrDefault(x => x.UserID == user.ID &&
                x.Fingerprint.Sha256.Equals(request.Fingerprint.Sha256, StringComparison.OrdinalIgnoreCase) &&
                x.Fingerprint.FileSize == request.Fingerprint.FileSize &&
                x.AgreementVersion == request.AgreementVersion);
            if (existing is not null) return existing;
            var record = new ConversionConsentRecord(
                Guid.NewGuid(), user.ID, user.Email, user.DisplayName, request.Fingerprint,
                request.SourceFileName.Trim(), request.AgreementVersion.Trim(),
                request.AgreementText.Trim(), DateTimeOffset.UtcNow);
            _records.Add(record);
            File.WriteAllText(_path, JsonSerializer.Serialize(_records));
            return record;
        }
    }

    public IReadOnlyList<ConversionConsentRecord> Search(string? query, int limit)
    {
        lock (_lock)
        {
            var value = query?.Trim();
            return _records.Where(x => string.IsNullOrEmpty(value) ||
                    x.ID.ToString().Contains(value, StringComparison.OrdinalIgnoreCase) ||
                    x.UserID.ToString().Contains(value, StringComparison.OrdinalIgnoreCase) ||
                    x.UserEmail.Contains(value, StringComparison.OrdinalIgnoreCase) ||
                    x.UserDisplayName.Contains(value, StringComparison.OrdinalIgnoreCase) ||
                    x.SourceFileName.Contains(value, StringComparison.OrdinalIgnoreCase) ||
                    x.Fingerprint.Sha256.Contains(value, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.AcceptedAt).Take(Math.Clamp(limit, 1, 200)).ToArray();
        }
    }

    public ConversionConsentRecord? Find(Guid id)
    {
        lock (_lock) return _records.FirstOrDefault(x => x.ID == id);
    }

    internal static void Validate(ConversionConsentRequest request)
    {
        if (request.Fingerprint.FileSize <= 0 || request.Fingerprint.Sha256.Length != 64 ||
            string.IsNullOrWhiteSpace(request.SourceFileName) || request.SourceFileName.Length > 500 ||
            string.IsNullOrWhiteSpace(request.AgreementVersion) || request.AgreementVersion.Length > 50 ||
            string.IsNullOrWhiteSpace(request.AgreementText) || request.AgreementText.Length > 5_000)
            throw new ArgumentException("The conversion acknowledgment is invalid.");
    }
}

#if POSTGRES
public sealed class PostgresConversionConsentStore(Npgsql.NpgsqlDataSource dataSource) : IConversionConsentStore
{
    public ConversionConsentRecord Record(AuthUser user, ConversionConsentRequest request)
    {
        FileConversionConsentStore.Validate(request);
        request = request with
        {
            Fingerprint = request.Fingerprint.Version <= 0
                ? request.Fingerprint with { Version = 1 }
                : request.Fingerprint
        };
        using var connection = dataSource.OpenConnection();
        using var command = new Npgsql.NpgsqlCommand("""
            insert into conversion_consents
                (id, user_id, user_email, user_display_name, fingerprint_version, sha256,
                 file_size, duration_seconds, file_type, source_file_name, agreement_version,
                 agreement_text, accepted_at)
            values ($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,now())
            on conflict (user_id, fingerprint_version, sha256, file_size, agreement_version)
            do update set source_file_name = conversion_consents.source_file_name
            returning id, accepted_at;
            """, connection);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(user.ID);
        command.Parameters.AddWithValue(user.Email);
        command.Parameters.AddWithValue(user.DisplayName);
        command.Parameters.AddWithValue(request.Fingerprint.Version);
        command.Parameters.AddWithValue(request.Fingerprint.Sha256.ToUpperInvariant());
        command.Parameters.AddWithValue(request.Fingerprint.FileSize);
        command.Parameters.AddWithValue(
            NpgsqlTypes.NpgsqlDbType.Double,
            (object?)request.Fingerprint.Duration ?? DBNull.Value);
        command.Parameters.AddWithValue(request.Fingerprint.FileType);
        command.Parameters.AddWithValue(request.SourceFileName.Trim());
        command.Parameters.AddWithValue(request.AgreementVersion.Trim());
        command.Parameters.AddWithValue(request.AgreementText.Trim());
        using var reader = command.ExecuteReader(); reader.Read();
        return new ConversionConsentRecord(reader.GetGuid(0), user.ID, user.Email, user.DisplayName,
            request.Fingerprint, request.SourceFileName.Trim(), request.AgreementVersion.Trim(),
            request.AgreementText.Trim(), reader.GetFieldValue<DateTimeOffset>(1));
    }

    public IReadOnlyList<ConversionConsentRecord> Search(string? query, int limit)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new Npgsql.NpgsqlCommand("""
            select id,user_id,user_email,user_display_name,fingerprint_version,sha256,file_size,
                   duration_seconds,file_type,source_file_name,agreement_version,agreement_text,accepted_at
            from conversion_consents
            where $1 = '' or id::text ilike '%' || $1 || '%' or user_id::text ilike '%' || $1 || '%'
               or user_email ilike '%' || $1 || '%' or user_display_name ilike '%' || $1 || '%'
               or source_file_name ilike '%' || $1 || '%' or sha256 ilike '%' || $1 || '%'
            order by accepted_at desc limit $2;
            """, connection);
        command.Parameters.AddWithValue(query?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue(Math.Clamp(limit, 1, 200));
        using var reader = command.ExecuteReader();
        var values = new List<ConversionConsentRecord>();
        while (reader.Read()) values.Add(new ConversionConsentRecord(
            reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3),
            new BookFingerprint(reader.GetInt32(4), reader.GetString(5), reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetDouble(7), reader.GetString(8), null, null,
                null, null, null, null, null),
            reader.GetString(9), reader.GetString(10), reader.GetString(11),
            reader.GetFieldValue<DateTimeOffset>(12)));
        return values;
    }

    public ConversionConsentRecord? Find(Guid id) => Search(id.ToString(), 1)
        .FirstOrDefault(x => x.ID == id);
}
#endif
