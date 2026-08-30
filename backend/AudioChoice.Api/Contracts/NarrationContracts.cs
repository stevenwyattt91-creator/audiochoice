namespace AudioChoice.Api.Contracts;

/// <summary>
/// Values shared by the audio and text scanning paths, so neither can drift from the other
/// by way of a copied literal.
/// </summary>
public static class ScanContracts
{
    /// <summary>
    /// The content taxonomy the events were drawn from.
    /// </summary>
    /// <remarks>
    /// Must match <see cref="CloudScanResponse"/>'s default. A narrated book's filter
    /// switches are the same switches an audiobook's are, and they are matched by category
    /// and group identifier, so a taxonomy disagreement would present a listener with
    /// toggles that control nothing. A contract test asserts the two agree.
    /// </remarks>
    public const string TaxonomyVersion = "2.0";
}

/// <summary>
/// A request to find filterable content in a book's text, for a book with no audiobook.
/// </summary>
public sealed record NarrationTextScanRequest(
    BookFingerprint Fingerprint,
    string BookText,
    string? Language = null)
{
    /// <summary>
    /// Prints the text's length instead of the text.
    /// </summary>
    /// <remarks>
    /// The generated <c>ToString</c> for a record prints every member, so one logger call
    /// that happens to include the request -- a log scope, an unhandled-exception dump, a
    /// developer adding <c>{Request}</c> to a message -- would write an entire novel into a
    /// log sink that is retained, shipped and searchable. That is the same disclosure the
    /// endpoint exists to avoid, arriving through the back door, so the safe rendering is
    /// the default one rather than something each call site must remember.
    /// </remarks>
    public override string ToString() =>
        $"NarrationTextScanRequest {{ Sha256 = {Fingerprint?.Sha256}, " +
        $"BookTextCharacters = {BookText?.Length ?? 0}, Language = {Language} }}";
}

/// <summary>
/// Filter events for a book's text, in character offsets into that text.
/// </summary>
/// <remarks>
/// Carries no part of the text back. The offsets say where a passage is, and the client
/// already holds the book, so returning the words would add nothing but exposure.
/// </remarks>
public sealed record NarrationTextScanResponse(
    IReadOnlyList<ScanEvent> Events,
    DateTimeOffset ScanDate,
    string ScannerVersion,
    string TaxonomyVersion,
    int BookTextCharacters);

/// <summary>
/// A completed text scan, as the pipeline produces it and the store records it.
/// </summary>
/// <remarks>
/// <see cref="BookTextCharacters"/> is kept because it is what makes an out-of-range event
/// offset detectable later without keeping the text it indexes.
/// </remarks>
public sealed record NarrationTextScan(
    IReadOnlyList<ScanEvent> Events,
    DateTimeOffset ScanDate,
    string ScannerVersion,
    string TaxonomyVersion,
    int BookTextCharacters,
    string? Language)
{
    public NarrationTextScanResponse ToResponse() =>
        new(Events, ScanDate, ScannerVersion, TaxonomyVersion, BookTextCharacters);
}

/// <summary>
/// The listener's agreement to premium synthesis, which sends a chapter's text off the device.
/// </summary>
public sealed record NarrationAgreement(
    string Version,
    string Text);

/// <summary>Voices a listener may choose, with the agreement that premium requires.</summary>
public sealed record NarrationVoicesResponse(
    IReadOnlyList<NarrationVoiceDescriptor> Voices,
    string AgreementVersion,
    string AgreementText);

/// <summary>One voice, with a fixed pre-rendered sample rather than one made on demand.</summary>
public sealed record NarrationVoiceDescriptor(
    string VoiceID,
    string DisplayName,
    string Language,
    string Provider,
    string SampleUrl);

/// <summary>Records that a listener accepted the premium synthesis agreement.</summary>
public sealed record NarrationAcknowledgementRequest(
    string AgreementVersion,
    string AgreementText);

public sealed record NarrationAcknowledgementResponse(
    string AgreementVersion,
    DateTimeOffset AcceptedAt);

/// <summary>
/// Asks for one chapter to be spoken.
/// </summary>
/// <remarks>
/// Units arrive with filtered characters already removed, so nothing the listener asked to have
/// filtered is ever sent. The <c>ToString</c> override is what keeps a log scope from printing
/// the chapter.
/// </remarks>
public sealed record NarrationChapterRequest(
    BookFingerprint Fingerprint,
    int ChapterIndex,
    string VoiceID,
    string? Language,
    IReadOnlyList<NarrationUnitRequest> Units)
{
    public int CharacterCount => Units.Sum(unit => unit.Text.Length);

    public override string ToString() =>
        $"NarrationChapterRequest {{ Sha256 = {Fingerprint?.Sha256}, " +
        $"ChapterIndex = {ChapterIndex}, VoiceID = {VoiceID}, " +
        $"Units = {Units.Count}, Characters = {CharacterCount} }}";
}

public sealed record NarrationUnitRequest(
    int StartCharacter,
    int EndCharacter,
    string Text);

public sealed record NarrationChapterAccepted(Guid JobID, string Status);

/// <summary>
/// A chapter job's state, and its audio once there is any.
/// </summary>
/// <remarks>
/// <see cref="AudioBase64"/> carries the finished chapter. Returned in the response rather than
/// through a signed URL to a storage container: chapter audio is derived closely enough from a
/// listener's book that keeping it in cloud storage would be the same disclosure the text
/// handling is careful to avoid, and it would sit there under a retention policy rather than
/// under the absence of anywhere to put it.
/// </remarks>
public sealed record NarrationChapterStatus(
    Guid JobID,
    int ChapterIndex,
    string Status,
    string? Provider = null,
    string? ModelVersion = null,
    string? VoiceID = null,
    double DurationSeconds = 0,
    IReadOnlyList<NarrationUnitTiming>? Timings = null,
    string? AudioBase64 = null,
    string? Error = null)
{
    public override string ToString() =>
        $"NarrationChapterStatus {{ JobID = {JobID}, ChapterIndex = {ChapterIndex}, " +
        $"Status = {Status}, Provider = {Provider}, DurationSeconds = {DurationSeconds:F1}, " +
        $"Timings = {Timings?.Count ?? 0}, AudioBase64Length = {AudioBase64?.Length ?? 0} }}";
}

/// <summary>Where one unit's audio sits, measured from the chapter's own start.</summary>
public sealed record NarrationUnitTiming(
    int StartCharacter,
    int EndCharacter,
    double StartSeconds,
    double EndSeconds);
