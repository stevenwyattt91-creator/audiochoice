#if POSTGRES
using System.Security.Cryptography;
using AudioChoice.Api.Contracts;
using Npgsql;

namespace AudioChoice.Api.Services;

public sealed class PostgresAffiliateStore(NpgsqlDataSource dataSource) : IAffiliateStore
{
    public IReadOnlyList<AffiliateSummary> List()
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            select a.id, a.code, a.label, a.email, a.active, a.created_at,
                   (select count(*)::int from affiliate_referrals r where r.affiliate_id = a.id)
            from affiliates a
            order by a.label;
            """, connection);
        using var reader = command.ExecuteReader();
        var values = new List<AffiliateSummary>();
        while (reader.Read()) values.Add(ReadSummary(reader));
        return values;
    }

    public AffiliateDetail? Detail(Guid affiliateID)
    {
        using var connection = dataSource.OpenConnection();
        AffiliateSummary? summary;
        using (var command = new NpgsqlCommand("""
            select a.id, a.code, a.label, a.email, a.active, a.created_at,
                   (select count(*)::int from affiliate_referrals r where r.affiliate_id = a.id)
            from affiliates a where a.id = $1;
            """, connection))
        {
            command.Parameters.AddWithValue(affiliateID);
            using var reader = command.ExecuteReader();
            summary = reader.Read() ? ReadSummary(reader) : null;
        }
        if (summary is null) return null;

        var referrals = new List<AffiliateReferral>();
        using var referralCommand = new NpgsqlCommand("""
            select r.user_id, u.email, u.display_name, r.attributed_at
            from affiliate_referrals r join users u on u.id = r.user_id
            where r.affiliate_id = $1
            order by r.attributed_at desc;
            """, connection);
        referralCommand.Parameters.AddWithValue(affiliateID);
        using var referralReader = referralCommand.ExecuteReader();
        while (referralReader.Read())
        {
            referrals.Add(new AffiliateReferral(
                referralReader.GetGuid(0), referralReader.GetString(1), referralReader.GetString(2),
                referralReader.GetFieldValue<DateTimeOffset>(3)));
        }
        return new AffiliateDetail(summary, referrals);
    }

    public AffiliateSummary Create(CreateAffiliateRequest request)
    {
        var label = Clean(request.Label, 200) ?? throw new ArgumentException("A label is required.");
        var email = Clean(request.Email, 254);

        using var connection = dataSource.OpenConnection();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var id = Guid.NewGuid();
            var code = RandomCode();
            try
            {
                using var command = new NpgsqlCommand("""
                    insert into affiliates(id, code, label, email, active, created_at)
                    values ($1, $2, $3, $4, true, now())
                    returning created_at;
                    """, connection);
                command.Parameters.AddWithValue(id);
                command.Parameters.AddWithValue(code);
                command.Parameters.AddWithValue(label);
                command.Parameters.AddWithValue((object?)email ?? DBNull.Value);
                var createdAt = (DateTimeOffset)command.ExecuteScalar()!;
                return new AffiliateSummary(id, code, label, email, true, createdAt, 0);
            }
            catch (PostgresException error) when (error.SqlState == "23505")
            {
                // Codes are eight random characters over a 33-symbol alphabet -- astronomically
                // unlikely to collide, but a fresh random draw costs nothing next to failing the
                // whole request over it.
            }
        }
        throw new InvalidOperationException("Could not generate a unique affiliate code.");
    }

    public bool SetActive(Guid affiliateID, bool active)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(
            "update affiliates set active = $1 where id = $2;", connection);
        command.Parameters.AddWithValue(active);
        command.Parameters.AddWithValue(affiliateID);
        return command.ExecuteNonQuery() == 1;
    }

    public bool CodeIsValid(string code)
    {
        var normalized = NormalizeCode(code);
        if (normalized is null) return false;
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(
            "select exists(select 1 from affiliates where lower(code) = lower($1) and active);", connection);
        command.Parameters.AddWithValue(normalized);
        return (bool)(command.ExecuteScalar() ?? false);
    }

    public void Attribute(Guid userID, string code, string email, string displayName)
    {
        var normalized = NormalizeCode(code);
        if (normalized is null) return;
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            insert into affiliate_referrals(user_id, affiliate_id, attributed_at)
            select $1, a.id, now() from affiliates a
            where lower(a.code) = lower($2) and a.active
            on conflict (user_id) do nothing;
            """, connection);
        command.Parameters.AddWithValue(userID);
        command.Parameters.AddWithValue(normalized);
        command.ExecuteNonQuery();
    }

    private static AffiliateSummary ReadSummary(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetBoolean(4),
        reader.GetFieldValue<DateTimeOffset>(5), reader.GetInt32(6));

    private static string RandomCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I, easy to read aloud
        var bytes = RandomNumberGenerator.GetBytes(8);
        return new string(bytes.Select(value => alphabet[value % alphabet.Length]).ToArray());
    }

    private static string? Clean(string? value, int maximum) => string.IsNullOrWhiteSpace(value)
        ? null : value.Trim()[..Math.Min(value.Trim().Length, maximum)];
    private static string? NormalizeCode(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToUpperInvariant();
    }
}
#endif
