using System.Security.Cryptography;
using System.Text.Json;
using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

public interface IAffiliateStore
{
    IReadOnlyList<AffiliateSummary> List();
    AffiliateDetail? Detail(Guid affiliateID);
    AffiliateSummary Create(CreateAffiliateRequest request);
    bool SetActive(Guid affiliateID, bool active);

    /// <summary>Whether a code names an active affiliate.</summary>
    bool CodeIsValid(string code);

    /// <summary>
    /// Records that <paramref name="userID"/> signed up using <paramref name="code"/>.
    /// </summary>
    /// <remarks>
    /// Silently does nothing for an unknown, inactive, or already-attributed account rather than
    /// throwing. Called right after registration, where a typo in a referral code or a resubmitted
    /// request must never be the reason an account fails to create. <paramref name="email"/> and
    /// <paramref name="displayName"/> are the registering user's own, captured here rather than
    /// looked up later so an affiliate's referral list never has to join back out to the accounts
    /// table (or its own separate file store, in the non-Postgres fallback).
    /// </remarks>
    void Attribute(Guid userID, string code, string email, string displayName);
}

/// <summary>
/// Development fallback. Production uses PostgresAffiliateStore so referral
/// codes and attributions are shared by Android, iOS, and the admin portal.
/// </summary>
public sealed class FileAffiliateStore(AudioChoiceDataPaths paths) : IAffiliateStore
{
    private readonly object _lock = new();
    private readonly string _path = paths.Affiliates;
    private State _state = Load(paths.Affiliates);

    public IReadOnlyList<AffiliateSummary> List()
    {
        lock (_lock)
        {
            return _state.Affiliates
                .OrderBy(value => value.Label, StringComparer.OrdinalIgnoreCase)
                .Select(value => ToSummary(value, _state.Referrals.Count(r => r.AffiliateID == value.ID)))
                .ToArray();
        }
    }

    public AffiliateDetail? Detail(Guid affiliateID)
    {
        lock (_lock)
        {
            var affiliate = _state.Affiliates.FirstOrDefault(value => value.ID == affiliateID);
            if (affiliate is null) return null;
            var referrals = _state.Referrals
                .Where(value => value.AffiliateID == affiliateID)
                .OrderByDescending(value => value.AttributedAt)
                .Select(value => new AffiliateReferral(value.UserID, value.Email, value.DisplayName, value.AttributedAt))
                .ToArray();
            return new AffiliateDetail(ToSummary(affiliate, referrals.Length), referrals);
        }
    }

    public AffiliateSummary Create(CreateAffiliateRequest request)
    {
        var label = Clean(request.Label, 200) ?? throw new ArgumentException("A label is required.");
        var email = Clean(request.Email, 254);
        lock (_lock)
        {
            var code = GenerateUniqueCode();
            var record = new AffiliateRecord(Guid.NewGuid(), code, label, email, true, DateTimeOffset.UtcNow);
            _state.Affiliates.Add(record);
            Persist();
            return ToSummary(record, 0);
        }
    }

    public bool SetActive(Guid affiliateID, bool active)
    {
        lock (_lock)
        {
            var index = _state.Affiliates.FindIndex(value => value.ID == affiliateID);
            if (index < 0) return false;
            _state.Affiliates[index] = _state.Affiliates[index] with { Active = active };
            Persist();
            return true;
        }
    }

    public bool CodeIsValid(string code)
    {
        var normalized = NormalizeCode(code);
        if (normalized is null) return false;
        lock (_lock)
        {
            return _state.Affiliates.Any(value =>
                value.Active && string.Equals(value.Code, normalized, StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Attribute(Guid userID, string code, string email, string displayName)
    {
        var normalized = NormalizeCode(code);
        if (normalized is null) return;
        lock (_lock)
        {
            var affiliate = _state.Affiliates.FirstOrDefault(value =>
                value.Active && string.Equals(value.Code, normalized, StringComparison.OrdinalIgnoreCase));
            if (affiliate is null) return;
            if (_state.Referrals.Any(value => value.UserID == userID)) return;
            _state.Referrals.Add(new ReferralRecord(userID, affiliate.ID, email, displayName, DateTimeOffset.UtcNow));
            Persist();
        }
    }

    private string GenerateUniqueCode()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var candidate = RandomCode();
            if (!_state.Affiliates.Any(value => string.Equals(value.Code, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
        throw new InvalidOperationException("Could not generate a unique affiliate code.");
    }

    private static string RandomCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O/1/I, easy to read aloud
        var bytes = RandomNumberGenerator.GetBytes(8);
        return new string(bytes.Select(value => alphabet[value % alphabet.Length]).ToArray());
    }

    private static AffiliateSummary ToSummary(AffiliateRecord value, int referralCount) => new(
        value.ID, value.Code, value.Label, value.Email, value.Active, value.CreatedAt, referralCount);

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_state));
        File.Move(temporary, _path, true);
    }

    private static State Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    private static string? Clean(string? value, int maximum) => string.IsNullOrWhiteSpace(value)
        ? null : value.Trim()[..Math.Min(value.Trim().Length, maximum)];
    private static string? NormalizeCode(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.ToUpperInvariant();
    }

    public sealed class State
    {
        public List<AffiliateRecord> Affiliates { get; init; } = [];
        public List<ReferralRecord> Referrals { get; init; } = [];
    }
    public sealed record AffiliateRecord(Guid ID, string Code, string Label, string? Email, bool Active, DateTimeOffset CreatedAt);
    public sealed record ReferralRecord(Guid UserID, Guid AffiliateID, string Email, string DisplayName, DateTimeOffset AttributedAt);
}
