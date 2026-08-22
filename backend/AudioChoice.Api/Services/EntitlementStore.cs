using System.Text.Json;
using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

public interface IEntitlementStore
{
    AccountAccessResponse Access(Guid userID);
    AccountAccessResponse Grant(Guid userID, EntitlementGrantRequest request);
}

/// <summary>
/// Development fallback. Production uses PostgresEntitlementStore so grants are
/// shared by Android, iOS, web, and the future desktop companion.
/// </summary>
public sealed class FileEntitlementStore(AudioChoiceDataPaths paths) : IEntitlementStore
{
    private readonly object _lock = new();
    private readonly string _path = paths.Entitlements;
    private State _state = Load(paths.Entitlements);

    public AccountAccessResponse Access(Guid userID)
    {
        lock (_lock) return ToAccess(_state.Grants.Where(value => value.UserID == userID));
    }

    public AccountAccessResponse Grant(Guid userID, EntitlementGrantRequest request)
    {
        var plan = Clean(request.Plan, 80);
        var source = Clean(request.Source, 40);
        if (plan is null || source is null) throw new ArgumentException("A plan and source are required.");
        if (request.ExpiresAt is { } expiresAt && expiresAt <= DateTimeOffset.UtcNow)
            throw new ArgumentException("An entitlement expiration must be in the future.");

        lock (_lock)
        {
            var reference = Clean(request.ExternalReference, 200);
            if (reference is not null)
            {
                _state.Grants.RemoveAll(value => value.UserID == userID &&
                    value.Source.Equals(source, StringComparison.OrdinalIgnoreCase) &&
                    value.ExternalReference == reference);
            }
            _state.Grants.Add(new EntitlementGrant(userID, plan, source, request.ExpiresAt, reference, DateTimeOffset.UtcNow));
            Persist();
            return ToAccess(_state.Grants.Where(value => value.UserID == userID));
        }
    }

    private static AccountAccessResponse ToAccess(IEnumerable<EntitlementGrant> grants)
    {
        var now = DateTimeOffset.UtcNow;
        var active = grants.Where(value => value.ExpiresAt is null || value.ExpiresAt > now)
            .OrderByDescending(value => value.ExpiresAt is null)
            .ThenByDescending(value => value.GrantedAt)
            .FirstOrDefault();
        return active is null
            ? new AccountAccessResponse(false, "free", "none", null, false, false)
            : new AccountAccessResponse(true, active.Plan, active.Source, active.ExpiresAt, true, true);
    }

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

    public sealed class State { public List<EntitlementGrant> Grants { get; init; } = []; }
    public sealed record EntitlementGrant(Guid UserID, string Plan, string Source, DateTimeOffset? ExpiresAt, string? ExternalReference, DateTimeOffset GrantedAt);
}
