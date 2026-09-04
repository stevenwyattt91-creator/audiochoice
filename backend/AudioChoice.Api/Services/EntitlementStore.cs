using System.Text.Json;
using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

public interface IEntitlementStore
{
    AccountAccessResponse Access(Guid userID);
    AccountAccessResponse Grant(Guid userID, EntitlementGrantRequest request);

    /// <summary>
    /// Ends a specific grant immediately, identified by <paramref name="source"/> and
    /// <paramref name="externalReference"/> alone -- no user id, because
    /// <c>account_entitlements_source_reference</c> already guarantees at most one active grant per
    /// source+reference, and a store's cancellation/refund notification names its own transaction or
    /// order id, never our internal user id.
    /// </summary>
    /// <remarks>
    /// Used for a refund or cancellation reported by a store, where access must end now rather than
    /// wait for the grant's own expiry -- the two are different events (a subscription that lapses
    /// on schedule vs. one Apple or Google reversed early) and only the second needs this.
    /// </remarks>
    bool Revoke(string source, string externalReference);
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

    public bool Revoke(string source, string externalReference)
    {
        lock (_lock)
        {
            var removed = _state.Grants.RemoveAll(value =>
                value.Source.Equals(source, StringComparison.OrdinalIgnoreCase) &&
                value.ExternalReference == externalReference);
            if (removed > 0) Persist();
            return removed > 0;
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
