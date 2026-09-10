#if POSTGRES
using Npgsql;

namespace AudioChoice.Api.Services;

public sealed class PostgresDevicePushTokenStore(NpgsqlDataSource dataSource) : IDevicePushTokenStore
{
    public void Register(Guid userID, string platform, string token)
    {
        using var connection = dataSource.OpenConnection();
        // Conflicts on the token, not on (user, token). The same physical device signing into a
        // second account has to move to that account rather than end up registered to both, or a
        // scan on the abandoned account would keep notifying it.
        using var command = new NpgsqlCommand("""
            insert into device_push_tokens(user_id, platform, token, created_at, updated_at)
            values ($1, $2, $3, now(), now())
            on conflict (token) do update set
                user_id = excluded.user_id,
                platform = excluded.platform,
                updated_at = now();
            """, connection);
        command.Parameters.AddWithValue(userID);
        command.Parameters.AddWithValue(platform.ToLowerInvariant());
        command.Parameters.AddWithValue(token);
        command.ExecuteNonQuery();
    }

    public void Remove(string token)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(
            "delete from device_push_tokens where token = $1;", connection);
        command.Parameters.AddWithValue(token);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<DevicePushToken> ForUsers(IEnumerable<Guid> userIDs)
    {
        var wanted = userIDs.Distinct().ToArray();
        if (wanted.Length == 0) return [];
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(
            "select user_id, platform, token from device_push_tokens where user_id = any($1);",
            connection);
        command.Parameters.AddWithValue(wanted);
        using var reader = command.ExecuteReader();
        var tokens = new List<DevicePushToken>();
        while (reader.Read())
        {
            tokens.Add(new DevicePushToken(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
        }
        return tokens;
    }
}
#endif
