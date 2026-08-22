using System.Security.Cryptography;
using System.Text.Json;

namespace AudioChoice.Api.Services;

public sealed record CompanionTransferRecord(
    Guid ID,
    Guid OwnerUserID,
    string FileName,
    string ContentType,
    long FileSize,
    string Sha256,
    string ReceiverCodeHash,
    DateTimeOffset ExpiresAt,
    string Status = "authorized");

public interface ICompanionTransferStore
{
    CompanionTransferRecord Create(Guid ownerUserID, string fileName, string contentType, long fileSize, string sha256, DateTimeOffset expiresAt, string receiverCode);
    CompanionTransferRecord? Find(Guid transferID);
    bool MarkUploaded(Guid transferID);
    bool MarkReceived(Guid transferID);
    IReadOnlyList<CompanionTransferRecord> Expired(DateTimeOffset now);
    void MarkDeleted(Guid transferID);
}

/// <summary>Development fallback. Production uses PostgreSQL.</summary>
public sealed class FileCompanionTransferStore(AudioChoiceDataPaths paths) : ICompanionTransferStore
{
    private readonly object _lock = new();
    private readonly string _path = paths.CompanionTransfers;
    private State _state = Load(paths.CompanionTransfers);

    public CompanionTransferRecord Create(Guid ownerUserID, string fileName, string contentType, long fileSize, string sha256, DateTimeOffset expiresAt, string receiverCode)
    {
        lock (_lock)
        {
            var value = new CompanionTransferRecord(Guid.NewGuid(), ownerUserID, Path.GetFileName(fileName), contentType, fileSize, sha256, Hash(receiverCode), expiresAt);
            _state.Transfers.Add(value);
            Persist();
            return value;
        }
    }

    public CompanionTransferRecord? Find(Guid transferID) { lock (_lock) return _state.Transfers.FirstOrDefault(value => value.ID == transferID); }
    public bool MarkUploaded(Guid transferID) => SetStatus(transferID, "uploaded");
    public bool MarkReceived(Guid transferID) => SetStatus(transferID, "received");
    public IReadOnlyList<CompanionTransferRecord> Expired(DateTimeOffset now) { lock (_lock) return _state.Transfers.Where(value => value.ExpiresAt <= now && value.Status is not "deleted" and not "received").ToList(); }
    public void MarkDeleted(Guid transferID) => SetStatus(transferID, "deleted");

    private bool SetStatus(Guid id, string status)
    {
        lock (_lock)
        {
            var index = _state.Transfers.FindIndex(value => value.ID == id);
            if (index < 0) return false;
            _state.Transfers[index] = _state.Transfers[index] with { Status = status };
            Persist();
            return true;
        }
    }
    private void Persist() { Directory.CreateDirectory(Path.GetDirectoryName(_path)!); File.WriteAllText(_path, JsonSerializer.Serialize(_state)); }
    private static State Load(string path) { try { return File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new() : new(); } catch (JsonException) { return new(); } }
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    public sealed class State { public List<CompanionTransferRecord> Transfers { get; init; } = []; }
}
