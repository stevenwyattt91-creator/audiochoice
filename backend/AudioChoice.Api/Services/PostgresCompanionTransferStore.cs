#if POSTGRES
using Npgsql;

namespace AudioChoice.Api.Services;

public sealed class PostgresCompanionTransferStore(NpgsqlDataSource dataSource) : ICompanionTransferStore
{
    public CompanionTransferRecord Create(Guid ownerUserID, string fileName, string contentType, long fileSize, string sha256, DateTimeOffset expiresAt, string receiverCode)
    {
        var record = new CompanionTransferRecord(Guid.NewGuid(), ownerUserID, Path.GetFileName(fileName), contentType, fileSize, sha256, FileCompanionTransferStore.Hash(receiverCode), expiresAt);
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            insert into companion_transfers(id, owner_user_id, file_name, content_type, expected_size, sha256, receiver_code_hash, status, expires_at, created_at)
            values ($1, $2, $3, $4, $5, $6, $7, 'authorized', $8, now());
            """, connection);
        command.Parameters.AddWithValue(record.ID); command.Parameters.AddWithValue(record.OwnerUserID);
        command.Parameters.AddWithValue(record.FileName); command.Parameters.AddWithValue(record.ContentType);
        command.Parameters.AddWithValue(record.FileSize); command.Parameters.AddWithValue(record.Sha256);
        command.Parameters.AddWithValue(record.ReceiverCodeHash); command.Parameters.AddWithValue(record.ExpiresAt);
        command.ExecuteNonQuery();
        return record;
    }

    public CompanionTransferRecord? Find(Guid transferID)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            select id, owner_user_id, file_name, content_type, expected_size, sha256, receiver_code_hash, expires_at, status
            from companion_transfers where id = $1;
            """, connection);
        command.Parameters.AddWithValue(transferID);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public bool MarkUploaded(Guid transferID) => SetStatus(transferID, "uploaded", "authorized");
    public bool MarkReceived(Guid transferID) => SetStatus(transferID, "received", "uploaded");
    public void MarkDeleted(Guid transferID) => SetStatus(transferID, "deleted", null);
    public IReadOnlyList<CompanionTransferRecord> Expired(DateTimeOffset now)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            select id, owner_user_id, file_name, content_type, expected_size, sha256, receiver_code_hash, expires_at, status
            from companion_transfers where expires_at <= $1 and status not in ('deleted', 'received');
            """, connection);
        command.Parameters.AddWithValue(now);
        using var reader = command.ExecuteReader(); var output = new List<CompanionTransferRecord>();
        while (reader.Read()) output.Add(Read(reader)); return output;
    }

    private bool SetStatus(Guid id, string status, string? expected)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(expected is null
            ? "update companion_transfers set status = $1 where id = $2;"
            : "update companion_transfers set status = $1 where id = $2 and status = $3 and expires_at > now();", connection);
        command.Parameters.AddWithValue(status); command.Parameters.AddWithValue(id); if (expected is not null) command.Parameters.AddWithValue(expected);
        return command.ExecuteNonQuery() > 0;
    }
    private static CompanionTransferRecord Read(NpgsqlDataReader value) => new(value.GetGuid(0), value.GetGuid(1), value.GetString(2), value.GetString(3), value.GetInt64(4), value.GetString(5), value.GetString(6), value.GetFieldValue<DateTimeOffset>(7), value.GetString(8));
}
#endif
