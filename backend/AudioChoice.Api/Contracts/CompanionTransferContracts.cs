namespace AudioChoice.Api.Contracts;

/// <summary>
/// A short-lived, account-paired handoff for an audiobook selected in the
/// companion app.
/// The companion receives the upload authorization; a device only receives a
/// download authorization after it authenticates as the same AudioChoice user.
/// </summary>
public sealed record CompanionTransferCreateRequest(
    string FileName,
    string ContentType,
    long FileSize,
    string Sha256);

public sealed record CompanionTransferUploadResponse(
    Guid TransferID,
    Uri UploadURL,
    string Method,
    IReadOnlyDictionary<string, string> Headers,
    string ReceiverURL,
    DateTimeOffset ExpiresAt);

public sealed record CompanionTransferClaimResponse(
    Guid TransferID,
    string FileName,
    string ContentType,
    long FileSize,
    string Sha256,
    Uri DownloadURL,
    DateTimeOffset ExpiresAt);
