namespace AudioChoice.Api.Contracts;

public sealed record ConversionConsentRequest(
    BookFingerprint Fingerprint,
    string SourceFileName,
    string AgreementVersion,
    string AgreementText);

public sealed record ConversionConsentRecord(
    Guid ID,
    Guid UserID,
    string UserEmail,
    string UserDisplayName,
    BookFingerprint Fingerprint,
    string SourceFileName,
    string AgreementVersion,
    string AgreementText,
    DateTimeOffset AcceptedAt);
