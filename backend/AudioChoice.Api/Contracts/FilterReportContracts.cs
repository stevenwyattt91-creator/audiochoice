namespace AudioChoice.Api.Contracts;

/// <summary>
/// What a listener is telling us the filter got wrong.
/// </summary>
public enum FilterReportKind
{
    /// <summary>Something played that should have been removed.</summary>
    MissedContent,

    /// <summary>Something was removed that should have played.</summary>
    WronglyFiltered
}

/// <summary>
/// A listener's report that filtering was wrong at a particular moment.
/// </summary>
/// <remarks>
/// Carries a timestamp and nothing else about what was heard. No audio, no transcript
/// text and no words travel with a report: the server already holds the edition's
/// transcript, so a position is enough to find the passage, and sending the content
/// would undo the guarantee that a listener's audio stays on their own device.
///
/// The window matters. Someone reacts, finds the button and taps, by which time the
/// passage is already several seconds behind, so a report describes the stretch of audio
/// ending at the tap rather than a single instant.
/// </remarks>
public sealed record FilterReportRequest(
    BookFingerprint Fingerprint,
    FilterReportKind Kind,
    double PositionSeconds,
    double? WindowSeconds = null,
    /// <summary>Which scan produced the result being reported, so a fixed scanner can be told apart from a bad edition match.</summary>
    string? ScannerVersion = null,
    /// <summary>Set when reporting a specific skip, which is what makes over-filtering reports actionable.</summary>
    Guid? ScanEventID = null,
    /// <summary>Optional category the listener picked, for triage only.</summary>
    Guid? CategoryID = null,
    /// <summary>
    /// What <see cref="PositionSeconds"/> measures: seconds of audio, or a character offset
    /// into a narrated book's text.
    /// </summary>
    /// <remarks>
    /// Optional with a null default, so a request from any already-shipped client deserialises
    /// unchanged and the endpoint shape does not move. Null means seconds.
    ///
    /// Anything other than the two known values is stored as seconds rather than rejected: a
    /// report is a one-off observation from someone who heard a mistake, and refusing it over
    /// an unrecognised unit would lose the only record that it happened.
    /// </remarks>
    string? PositionUnit = null);

public sealed record FilterReport(
    Guid ID,
    Guid AccountID,
    BookFingerprint Fingerprint,
    FilterReportKind Kind,
    double PositionSeconds,
    double WindowSeconds,
    string? ScannerVersion,
    Guid? ScanEventID,
    Guid? CategoryID,
    DateTimeOffset ReportedAt,
    /// <summary>
    /// Always populated, unlike the request's, so nothing reading a stored report has to guess.
    /// Defaulted last so existing positional construction keeps compiling.
    /// </summary>
    string PositionUnit = FilterReportPositionUnits.Seconds);

/// <summary>
/// What a report's position measures. Constrained rather than free text, because triage reading
/// a character offset as a timestamp is the failure this distinction exists to prevent.
/// </summary>
public static class FilterReportPositionUnits
{
    public const string Seconds = "seconds";
    public const string CharacterOffset = "characterOffset";

    /// <summary>
    /// Normalises a supplied unit, treating anything unrecognised as seconds.
    /// </summary>
    /// <remarks>
    /// Permissive on purpose. A report is a one-off observation from someone who heard a
    /// mistake, so an unrecognised unit is worth storing under the default rather than
    /// discarding along with the observation.
    /// </remarks>
    public static string Normalize(string? supplied) =>
        string.Equals(supplied, CharacterOffset, StringComparison.OrdinalIgnoreCase)
            ? CharacterOffset
            : Seconds;
}
