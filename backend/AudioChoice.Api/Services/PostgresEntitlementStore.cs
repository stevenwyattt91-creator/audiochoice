#if POSTGRES
using AudioChoice.Api.Contracts;
using Npgsql;

namespace AudioChoice.Api.Services;

public sealed class PostgresEntitlementStore(NpgsqlDataSource dataSource) : IEntitlementStore
{
    public AccountAccessResponse Access(Guid userID)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            select plan, source, expires_at
            from account_entitlements
            where user_id = $1 and revoked_at is null and (expires_at is null or expires_at > now())
            order by (expires_at is null) desc, granted_at desc
            limit 1;
            """, connection);
        command.Parameters.AddWithValue(userID);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new AccountAccessResponse(true, reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2), true, true)
            : new AccountAccessResponse(false, "free", "none", null, false, false);
    }

    public AccountAccessResponse Grant(Guid userID, EntitlementGrantRequest request)
    {
        var plan = Clean(request.Plan, 80);
        var source = Clean(request.Source, 40);
        if (plan is null || source is null) throw new ArgumentException("A plan and source are required.");
        if (request.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
            throw new ArgumentException("An entitlement expiration must be in the future.");

        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var reference = Clean(request.ExternalReference, 200);
        if (reference is not null)
        {
            using var remove = new NpgsqlCommand("""
                update account_entitlements set revoked_at = now()
                where user_id = $1 and source = $2 and external_reference = $3 and revoked_at is null;
                """, connection, transaction);
            remove.Parameters.AddWithValue(userID);
            remove.Parameters.AddWithValue(source);
            remove.Parameters.AddWithValue(reference);
            remove.ExecuteNonQuery();
        }
        using var insert = new NpgsqlCommand("""
            insert into account_entitlements(id, user_id, plan, source, external_reference, expires_at, granted_at)
            values ($1, $2, $3, $4, $5, $6, now());
            """, connection, transaction);
        insert.Parameters.AddWithValue(Guid.NewGuid());
        insert.Parameters.AddWithValue(userID);
        insert.Parameters.AddWithValue(plan);
        insert.Parameters.AddWithValue(source);
        insert.Parameters.AddWithValue((object?)reference ?? DBNull.Value);
        insert.Parameters.AddWithValue((object?)request.ExpiresAt ?? DBNull.Value);
        insert.ExecuteNonQuery();
        transaction.Commit();
        return Access(userID);
    }

    public bool Revoke(string source, string externalReference)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            update account_entitlements set revoked_at = now()
            where source = $1 and external_reference = $2 and revoked_at is null;
            """, connection);
        command.Parameters.AddWithValue(source);
        command.Parameters.AddWithValue(externalReference);
        return command.ExecuteNonQuery() > 0;
    }

    private static string? Clean(string? value, int maximum) => string.IsNullOrWhiteSpace(value)
        ? null : value.Trim()[..Math.Min(value.Trim().Length, maximum)];
}
#endif
