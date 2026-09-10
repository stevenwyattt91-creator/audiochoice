using System.Text.Json;

namespace AudioChoice.Api.Services;

/// <summary>
/// Which devices to notify for an account.
/// </summary>
public interface IDevicePushTokenStore
{
    /// <summary>
    /// Records a device against an account, replacing any earlier owner of the same token.
    /// </summary>
    /// <remarks>
    /// Keyed on the token rather than on the account, because a phone handed to someone else, or
    /// signed into a second account, keeps the same token. Without the replace, the previous
    /// account's scans would keep notifying a device that is no longer theirs.
    /// </remarks>
    void Register(Guid userID, string platform, string token);

    /// <summary>Forgets a device, on sign-out or when the transport reports it is gone.</summary>
    void Remove(string token);

    /// <summary>Every device registered to any of these accounts.</summary>
    IReadOnlyList<DevicePushToken> ForUsers(IEnumerable<Guid> userIDs);
}

public sealed record DevicePushToken(Guid UserID, string Platform, string Token);

public static class DevicePushPlatforms
{
    public const string Apns = "apns";
    public const string Fcm = "fcm";

    public static bool IsKnown(string? value) =>
        string.Equals(value, Apns, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, Fcm, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// File-backed store for builds running without Postgres.
/// </summary>
public sealed class FileDevicePushTokenStore : IDevicePushTokenStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private State _state;

    public FileDevicePushTokenStore(AudioChoiceDataPaths paths)
    {
        _path = Path.Combine(Path.GetDirectoryName(paths.Accounts)!, "device-push-tokens.json");
        _state = Load(_path);
    }

    public void Register(Guid userID, string platform, string token)
    {
        lock (_lock)
        {
            _state.Tokens.RemoveAll(value =>
                string.Equals(value.Token, token, StringComparison.Ordinal));
            _state.Tokens.Add(new DevicePushToken(userID, platform.ToLowerInvariant(), token));
            Persist();
        }
    }

    public void Remove(string token)
    {
        lock (_lock)
        {
            if (_state.Tokens.RemoveAll(value =>
                string.Equals(value.Token, token, StringComparison.Ordinal)) == 0) return;
            Persist();
        }
    }

    public IReadOnlyList<DevicePushToken> ForUsers(IEnumerable<Guid> userIDs)
    {
        var wanted = userIDs.ToHashSet();
        lock (_lock) return _state.Tokens.Where(value => wanted.Contains(value.UserID)).ToList();
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
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch (JsonException) { return new(); }
    }

    public sealed class State { public List<DevicePushToken> Tokens { get; init; } = []; }
}
