using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

public interface IAccountStore
{
    RegistrationResult? Register(RegisterRequest request);
    AuthResponse? Login(LoginRequest request);
    AuthResponse LoginExternal(string provider, string subject, string email, string? displayName);
    bool LinkExternal(Guid userID, string provider, string subject, string email);
    IReadOnlyList<string> ListIdentityProviders(Guid userID);
    AuthUser? Authenticate(string accessToken);
    bool VerifyEmail(string token);
    AccountActionToken? CreatePasswordReset(string email);
    bool ResetPassword(string token, string newPassword);
    void Logout(string accessToken);
    void LogoutAll(Guid userID);

    /// <summary>
    /// The account for an email address, or null when there is none.
    /// </summary>
    /// <remarks>
    /// Exists so an operator can act on the identifier they have. Every other administrative call
    /// takes a user id, which means reading one out of the database first -- and a mistyped id acts
    /// on whichever account it happens to match, silently.
    /// </remarks>
    Guid? FindUserIDByEmail(string email);
}

public sealed record RegistrationResult(
    AuthResponse Response,
    AccountActionToken Verification);

public sealed record AccountActionToken(
    string Email,
    string Token,
    DateTimeOffset ExpiresAt);

public sealed class FileAccountStore : IAccountStore
{
    private const int PasswordIterations = 210_000;
    private readonly string _path;
    private readonly object _lock = new();
    private AccountState _state;

    public FileAccountStore(string path)
    {
        _path = path;
        _state = Load(path);
    }

    public RegistrationResult? Register(RegisterRequest request)
    {
        var email = NormalizeEmail(request.Email);
        if (email is null || request.Password.Length < 12 || request.Password.Length > 256) return null;
        lock (_lock)
        {
            if (_state.Accounts.Any(value => value.Email == email)) return null;
            var salt = RandomNumberGenerator.GetBytes(32);
            var account = new AccountRecord(
                Guid.NewGuid(), email, CleanName(request.DisplayName, email), "password", email,
                Convert.ToBase64String(salt), Convert.ToBase64String(HashPassword(request.Password, salt)));
            _state.Accounts.Add(account);
            var response = CreateSession(account);
            var verification = CreateActionToken(
                account,
                _state.EmailVerifications,
                TimeSpan.FromHours(24));
            Persist();
            return new RegistrationResult(response, verification);
        }
    }

    public AuthResponse? Login(LoginRequest request)
    {
        var email = NormalizeEmail(request.Email);
        if (email is null) return null;
        lock (_lock)
        {
            var account = _state.Accounts.FirstOrDefault(value => value.Email == email && value.Provider == "password");
            if (account?.PasswordSalt is null || account.PasswordHash is null) return null;
            var computed = HashPassword(request.Password, Convert.FromBase64String(account.PasswordSalt));
            if (!CryptographicOperations.FixedTimeEquals(computed, Convert.FromBase64String(account.PasswordHash))) return null;
            var response = CreateSession(account);
            Persist();
            return response;
        }
    }

    public AuthResponse LoginExternal(string provider, string subject, string email, string? displayName)
    {
        lock (_lock)
        {
            provider = provider.Trim().ToLowerInvariant();
            var linked = _state.LinkedIdentities.FirstOrDefault(value =>
                value.Provider == provider && value.Subject == subject);
            var account = linked is null
                ? _state.Accounts.FirstOrDefault(value => value.Provider == provider && value.ProviderSubject == subject)
                : _state.Accounts.FirstOrDefault(value => value.ID == linked.AccountID);
            if (account is null)
            {
                var normalized = NormalizeEmail(email) ?? $"{subject}@private.invalid";
                account = provider == "google" && normalized.EndsWith("@gmail.com", StringComparison.OrdinalIgnoreCase)
                    ? _state.Accounts.FirstOrDefault(value => value.Email == normalized)
                    : null;
                if (account is null)
                {
                    account = new AccountRecord(Guid.NewGuid(), normalized,
                        CleanName(displayName, email), provider, subject, null, null, true);
                    _state.Accounts.Add(account);
                }
                else
                {
                    _state.LinkedIdentities.Add(new(account.ID, provider, subject));
                }
            }
            var response = CreateSession(account);
            Persist();
            return response;
        }
    }

    public bool LinkExternal(Guid userID, string provider, string subject, string email)
    {
        lock (_lock)
        {
            provider = provider.Trim().ToLowerInvariant();
            if (!_state.Accounts.Any(value => value.ID == userID) ||
                _state.Accounts.Any(value => value.Provider == provider && value.ProviderSubject == subject) ||
                _state.LinkedIdentities.Any(value => value.Provider == provider && value.Subject == subject)) return false;
            _state.LinkedIdentities.Add(new(userID, provider, subject));
            Persist();
            return true;
        }
    }

    public IReadOnlyList<string> ListIdentityProviders(Guid userID)
    {
        lock (_lock)
        {
            var primary = _state.Accounts.Where(value => value.ID == userID).Select(value => value.Provider);
            return primary.Concat(_state.LinkedIdentities.Where(value => value.AccountID == userID)
                .Select(value => value.Provider)).Distinct().Order().ToArray();
        }
    }

    public AuthUser? Authenticate(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return null;
        var hash = TokenHash(accessToken);
        lock (_lock)
        {
            var session = _state.Sessions.FirstOrDefault(value => value.TokenHash == hash && value.ExpiresAt > DateTimeOffset.UtcNow);
            var account = session is null ? null : _state.Accounts.FirstOrDefault(value => value.ID == session.AccountID);
            return account is null ? null : ToUser(account);
        }
    }

    public void Logout(string accessToken)
    {
        lock (_lock)
        {
            _state.Sessions.RemoveAll(value => value.TokenHash == TokenHash(accessToken));
            Persist();
        }
    }

    public void LogoutAll(Guid userID)
    {
        lock (_lock)
        {
            _state.Sessions.RemoveAll(value => value.AccountID == userID);
            Persist();
        }
    }

    public Guid? FindUserIDByEmail(string email)
    {
        var normalized = NormalizeEmail(email);
        if (normalized is null) return null;
        lock (_lock)
        {
            return _state.Accounts
                .FirstOrDefault(value =>
                    string.Equals(value.Email, normalized, StringComparison.OrdinalIgnoreCase))
                ?.ID;
        }
    }


    public bool VerifyEmail(string token)
    {
        var hash = TokenHash(token);
        lock (_lock)
        {
            PurgeExpiredActionTokens();
            var verification = _state.EmailVerifications.FirstOrDefault(
                value => value.TokenHash == hash);
            if (verification is null) return false;

            var accountIndex = _state.Accounts.FindIndex(
                value => value.ID == verification.AccountID);
            if (accountIndex < 0) return false;

            _state.Accounts[accountIndex] = _state.Accounts[accountIndex] with
            {
                EmailVerified = true
            };
            _state.EmailVerifications.RemoveAll(
                value => value.AccountID == verification.AccountID);
            Persist();
            return true;
        }
    }

    public AccountActionToken? CreatePasswordReset(string email)
    {
        var normalized = NormalizeEmail(email);
        if (normalized is null) return null;

        lock (_lock)
        {
            PurgeExpiredActionTokens();
            var account = _state.Accounts.FirstOrDefault(
                value => value.Email == normalized && value.Provider == "password");
            if (account is null) return null;

            var action = CreateActionToken(
                account,
                _state.PasswordResets,
                TimeSpan.FromHours(1));
            Persist();
            return action;
        }
    }

    public bool ResetPassword(string token, string newPassword)
    {
        if (!PasswordIsValid(newPassword)) return false;
        var hash = TokenHash(token);

        lock (_lock)
        {
            PurgeExpiredActionTokens();
            var reset = _state.PasswordResets.FirstOrDefault(
                value => value.TokenHash == hash);
            if (reset is null) return false;

            var accountIndex = _state.Accounts.FindIndex(
                value => value.ID == reset.AccountID && value.Provider == "password");
            if (accountIndex < 0) return false;

            var salt = RandomNumberGenerator.GetBytes(32);
            _state.Accounts[accountIndex] = _state.Accounts[accountIndex] with
            {
                PasswordSalt = Convert.ToBase64String(salt),
                PasswordHash = Convert.ToBase64String(HashPassword(newPassword, salt))
            };
            _state.PasswordResets.RemoveAll(value => value.AccountID == reset.AccountID);
            _state.Sessions.RemoveAll(value => value.AccountID == reset.AccountID);
            Persist();
            return true;
        }
    }

    private AuthResponse CreateSession(AccountRecord account)
    {
        _state.Sessions.RemoveAll(value => value.ExpiresAt <= DateTimeOffset.UtcNow);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(48));
        var expires = DateTimeOffset.UtcNow.AddDays(30);
        _state.Sessions.Add(new SessionRecord(TokenHash(token), account.ID, expires));
        return new AuthResponse(token, expires, ToUser(account));
    }

    private AccountActionToken CreateActionToken(
        AccountRecord account,
        List<AccountActionRecord> destination,
        TimeSpan lifetime)
    {
        destination.RemoveAll(value => value.AccountID == account.ID);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(48));
        var expiresAt = DateTimeOffset.UtcNow.Add(lifetime);
        destination.Add(new AccountActionRecord(
            TokenHash(token),
            account.ID,
            expiresAt));
        return new AccountActionToken(account.Email, token, expiresAt);
    }

    private void PurgeExpiredActionTokens()
    {
        var now = DateTimeOffset.UtcNow;
        _state.EmailVerifications.RemoveAll(value => value.ExpiresAt <= now);
        _state.PasswordResets.RemoveAll(value => value.ExpiresAt <= now);
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_state));
        File.Move(temporary, _path, true);
    }

    private static AccountState Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<AccountState>(File.ReadAllText(path)) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    private static byte[] HashPassword(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static string? NormalizeEmail(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        return normalized.Length is > 3 and <= 254 && normalized.Contains('@') ? normalized : null;
    }
    private static bool PasswordIsValid(string password) =>
        password.Length is >= 12 and <= 256;
    private static string CleanName(string? name, string fallback) =>
        string.IsNullOrWhiteSpace(name) ? fallback.Split('@')[0] : name.Trim()[..Math.Min(name.Trim().Length, 80)];
    private static AuthUser ToUser(AccountRecord value) => new(value.ID, value.Email, value.DisplayName, value.Provider);

    public sealed class AccountState
    {
        public List<AccountRecord> Accounts { get; init; } = [];
        public List<SessionRecord> Sessions { get; init; } = [];
        public List<AccountActionRecord> EmailVerifications { get; init; } = [];
        public List<AccountActionRecord> PasswordResets { get; init; } = [];
        public List<LinkedIdentityRecord> LinkedIdentities { get; init; } = [];
    }
    public sealed record AccountRecord(Guid ID, string Email, string DisplayName, string Provider, string ProviderSubject, string? PasswordSalt, string? PasswordHash, bool EmailVerified = false);
    public sealed record SessionRecord(string TokenHash, Guid AccountID, DateTimeOffset ExpiresAt);
    public sealed record AccountActionRecord(
        string TokenHash,
        Guid AccountID,
        DateTimeOffset ExpiresAt);
    public sealed record LinkedIdentityRecord(Guid AccountID, string Provider, string Subject);
}
