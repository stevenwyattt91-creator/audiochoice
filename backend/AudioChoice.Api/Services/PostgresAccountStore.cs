#if POSTGRES
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AudioChoice.Api.Contracts;
using Npgsql;

namespace AudioChoice.Api.Services;

public sealed class PostgresAccountStore(NpgsqlDataSource dataSource) : IAccountStore
{
    private const int PasswordIterations = 210_000;

    public RegistrationResult? Register(RegisterRequest request)
    {
        var email = NormalizeEmail(request.Email);
        if (email is null || !PasswordIsValid(request.Password)) return null;

        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        if (IdentityExists(connection, transaction, "password", email)) return null;

        var userID = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var salt = RandomNumberGenerator.GetBytes(32);
        Execute(connection, transaction, """
            insert into users(id, email, display_name, email_verified, created_at, updated_at)
            values ($1, $2, $3, false, $4, $4);
            """, userID, email, CleanName(request.DisplayName, email), now);
        Execute(connection, transaction, """
            insert into user_identities(
                id, user_id, provider, provider_subject, password_salt, password_hash, created_at)
            values ($1, $2, 'password', $3, $4, $5, $6);
            """, Guid.NewGuid(), userID, email, Convert.ToBase64String(salt),
            Convert.ToBase64String(HashPassword(request.Password, salt)), now);

        var user = new AuthUser(userID, email, CleanName(request.DisplayName, email), "password");
        var response = CreateSession(connection, transaction, user);
        var verification = CreateActionToken(
            connection, transaction, userID, email, "verify_email", TimeSpan.FromHours(24));
        transaction.Commit();
        return new RegistrationResult(response, verification);
    }

    public AuthResponse? Login(LoginRequest request)
    {
        var email = NormalizeEmail(request.Email);
        if (email is null) return null;
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            select u.id, u.email, u.display_name, i.password_salt, i.password_hash
            from user_identities i
            join users u on u.id = i.user_id
            where i.provider = 'password' and i.provider_subject = $1;
            """, connection);
        command.Parameters.AddWithValue(email);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var salt = Convert.FromBase64String(reader.GetString(3));
        var expected = Convert.FromBase64String(reader.GetString(4));
        var actual = HashPassword(request.Password, salt);
        if (!CryptographicOperations.FixedTimeEquals(actual, expected)) return null;
        var user = new AuthUser(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), "password");
        reader.Close();
        using var transaction = connection.BeginTransaction();
        var response = CreateSession(connection, transaction, user);
        transaction.Commit();
        return response;
    }

    public AuthResponse LoginExternal(
        string provider, string subject, string email, string? displayName)
    {
        provider = provider.Trim().ToLowerInvariant();
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var user = FindExternal(connection, transaction, provider, subject);
        if (user is null)
        {
            var normalized = NormalizeEmail(email) ?? $"{subject}@private.invalid";
            var now = DateTimeOffset.UtcNow;
            user = provider == "google" && normalized.EndsWith("@gmail.com", StringComparison.OrdinalIgnoreCase)
                ? FindUserByEmail(connection, transaction, normalized, provider)
                : null;
            if (user is null)
            {
                var userID = Guid.NewGuid();
                var name = CleanName(displayName, normalized);
                Execute(connection, transaction, """
                    insert into users(id, email, display_name, email_verified, created_at, updated_at)
                    values ($1, $2, $3, true, $4, $4);
                    """, userID, normalized, name, now);
                user = new AuthUser(userID, normalized, name, provider);
            }
            Execute(connection, transaction, """
                insert into user_identities(id, user_id, provider, provider_subject, created_at)
                values ($1, $2, $3, $4, $5);
                """, Guid.NewGuid(), user.ID, provider, subject, now);
        }
        var response = CreateSession(connection, transaction, user);
        transaction.Commit();
        return response;
    }

    public bool LinkExternal(Guid userID, string provider, string subject, string email)
    {
        provider = provider.Trim().ToLowerInvariant();
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        if (!UserExists(connection, transaction, userID) ||
            IdentityExists(connection, transaction, provider, subject)) return false;
        Execute(connection, transaction, """
            insert into user_identities(id, user_id, provider, provider_subject, created_at)
            values ($1, $2, $3, $4, now());
            """, Guid.NewGuid(), userID, provider, subject);
        transaction.Commit();
        return true;
    }

    public IReadOnlyList<string> ListIdentityProviders(Guid userID)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(
            "select distinct provider from user_identities where user_id = $1 order by provider;",
            connection);
        command.Parameters.AddWithValue(userID);
        using var reader = command.ExecuteReader();
        var providers = new List<string>();
        while (reader.Read()) providers.Add(reader.GetString(0));
        return providers;
    }

    public AuthUser? Authenticate(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return null;
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            select u.id, u.email, u.display_name, s.provider
            from user_sessions s join users u on u.id = s.user_id
            where s.token_hash = $1 and s.expires_at > now();
            """, connection);
        command.Parameters.AddWithValue(TokenHash(accessToken));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new AuthUser(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))
            : null;
    }

    public bool VerifyEmail(string token)
    {
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var userID = ConsumeActionToken(connection, transaction, token, "verify_email");
        if (userID is null) return false;
        Execute(connection, transaction,
            "update users set email_verified = true, updated_at = now() where id = $1;", userID.Value);
        transaction.Commit();
        return true;
    }

    public AccountActionToken? CreatePasswordReset(string email)
    {
        var normalized = NormalizeEmail(email);
        if (normalized is null) return null;
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = new NpgsqlCommand("""
            select u.id, u.email from user_identities i join users u on u.id = i.user_id
            where i.provider = 'password' and i.provider_subject = $1;
            """, connection, transaction);
        command.Parameters.AddWithValue(normalized);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var userID = reader.GetGuid(0);
        var storedEmail = reader.GetString(1);
        reader.Close();
        // Fifteen minutes, not an hour: a six-digit code is short enough that the window it is
        // guessable in matters, and a listener who asked for a code reads their email now rather than
        // later. Another can always be requested.
        var token = CreateActionToken(
            connection, transaction, userID, storedEmail, "reset_password",
            TimeSpan.FromMinutes(15), shortCode: true);
        transaction.Commit();
        return token;
    }

    public bool ResetPassword(string token, string newPassword)
    {
        if (!PasswordIsValid(newPassword)) return false;
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var userID = ConsumeActionToken(connection, transaction, token, "reset_password");
        if (userID is null) return false;
        var salt = RandomNumberGenerator.GetBytes(32);
        Execute(connection, transaction, """
            update user_identities set password_salt = $1, password_hash = $2
            where user_id = $3 and provider = 'password';
            """, Convert.ToBase64String(salt),
            Convert.ToBase64String(HashPassword(newPassword, salt)), userID.Value);
        Execute(connection, transaction,
            "delete from user_sessions where user_id = $1;", userID.Value);
        transaction.Commit();
        return true;
    }

    public void Logout(string accessToken)
    {
        using var connection = dataSource.OpenConnection();
        Execute(connection, null,
            "delete from user_sessions where token_hash = $1;", TokenHash(accessToken));
    }

    public void LogoutAll(Guid userID)
    {
        using var connection = dataSource.OpenConnection();
        Execute(connection, null, "delete from user_sessions where user_id = $1;", userID);
    }

    public Guid? FindUserIDByEmail(string email)
    {
        // Normalised the same way registration and sign-in do, so the address an operator types
        // matches the stored one regardless of case or surrounding space.
        var normalized = NormalizeEmail(email);
        if (normalized is null) return null;
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(
            "select id from users where email = $1;", connection);
        command.Parameters.AddWithValue(normalized);
        using var reader = command.ExecuteReader();
        return reader.Read() ? reader.GetGuid(0) : null;
    }


    private static AuthResponse CreateSession(
        NpgsqlConnection connection, NpgsqlTransaction transaction, AuthUser user)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(48));
        var expires = DateTimeOffset.UtcNow.AddDays(30);
        Execute(connection, transaction, """
            insert into user_sessions(token_hash, user_id, provider, expires_at, created_at)
            values ($1, $2, $3, $4, now());
            """, TokenHash(token), user.ID, user.Provider, expires);
        return new AuthResponse(token, expires, user);
    }

    /// <summary>
    /// Issues a single-use token for an account action.
    /// </summary>
    /// <param name="shortCode">
    /// When true the token is a six-digit number rather than 96 hex characters.
    /// <para>
    /// Used for a password reset, where the token is read out of an email and typed into a phone. A
    /// 96-character string is not something a person transcribes; they paste it or they give up, and
    /// pasting from a mail app is exactly where a stray newline creeps in.
    /// </para>
    /// <para>
    /// Six digits is a million possibilities, so the guard is rate and time rather than length. The
    /// authentication endpoints permit ten requests a minute, and a reset code lives fifteen minutes,
    /// which allows roughly 150 guesses against a code from one source -- about a 0.015% chance of
    /// hitting it before it expires. Any change to that rate limit or that lifetime changes this
    /// arithmetic, so they belong together.
    /// </para>
    /// <para>
    /// Only ever generated with a cryptographic source. A predictable six-digit code would be far
    /// worse than a short one.
    /// </para>
    /// </param>
    private static AccountActionToken CreateActionToken(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid userID, string email, string purpose, TimeSpan lifetime, bool shortCode = false)
    {
        Execute(connection, transaction,
            "delete from account_action_tokens where user_id = $1 and purpose = $2;", userID, purpose);
        var token = shortCode
            // Uniform across the whole range, unlike taking a modulus of a random integer.
            ? RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture)
            : Convert.ToHexString(RandomNumberGenerator.GetBytes(48));
        var expires = DateTimeOffset.UtcNow.Add(lifetime);
        Execute(connection, transaction, """
            insert into account_action_tokens(token_hash, user_id, purpose, expires_at, created_at)
            values ($1, $2, $3, $4, now());
            """, TokenHash(token), userID, purpose, expires);
        return new AccountActionToken(email, token, expires);
    }

    private static Guid? ConsumeActionToken(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string token, string purpose)
    {
        using var command = new NpgsqlCommand("""
            delete from account_action_tokens
            where token_hash = $1 and purpose = $2 and expires_at > now()
            returning user_id;
            """, connection, transaction);
        command.Parameters.AddWithValue(TokenHash(token));
        command.Parameters.AddWithValue(purpose);
        return command.ExecuteScalar() as Guid?;
    }

    private static bool IdentityExists(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string provider, string subject)
    {
        using var command = new NpgsqlCommand(
            "select exists(select 1 from user_identities where provider = $1 and provider_subject = $2);",
            connection, transaction);
        command.Parameters.AddWithValue(provider);
        command.Parameters.AddWithValue(subject);
        return (bool)(command.ExecuteScalar() ?? false);
    }

    private static AuthUser? FindExternal(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string provider, string subject)
    {
        using var command = new NpgsqlCommand("""
            select u.id, u.email, u.display_name
            from user_identities i join users u on u.id = i.user_id
            where i.provider = $1 and i.provider_subject = $2;
            """, connection, transaction);
        command.Parameters.AddWithValue(provider);
        command.Parameters.AddWithValue(subject);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new AuthUser(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), provider)
            : null;
    }

    private static AuthUser? FindUserByEmail(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string email,
        string provider)
    {
        using var command = new NpgsqlCommand("""
            select id, email, display_name from users
            where lower(email) = lower($1)
            order by created_at
            limit 1;
            """, connection, transaction);
        command.Parameters.AddWithValue(email);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new AuthUser(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), provider)
            : null;
    }

    private static bool UserExists(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userID)
    {
        using var command = new NpgsqlCommand(
            "select exists(select 1 from users where id = $1);", connection, transaction);
        command.Parameters.AddWithValue(userID);
        return (bool)(command.ExecuteScalar() ?? false);
    }

    private static void Execute(
        NpgsqlConnection connection, NpgsqlTransaction? transaction,
        string sql, params object[] values)
    {
        using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var value in values) command.Parameters.AddWithValue(value);
        command.ExecuteNonQuery();
    }

    private static byte[] HashPassword(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
    private static string TokenHash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static bool PasswordIsValid(string password) => password.Length is >= 12 and <= 256;
    private static string? NormalizeEmail(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Length is > 3 and <= 254 && normalized.Contains('@') ? normalized : null;
    }
    private static string CleanName(string? name, string fallback) =>
        string.IsNullOrWhiteSpace(name)
            ? fallback.Split('@')[0]
            : name.Trim()[..Math.Min(name.Trim().Length, 80)];
}
#endif
