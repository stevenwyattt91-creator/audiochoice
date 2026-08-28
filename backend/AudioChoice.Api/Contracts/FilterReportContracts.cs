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
    Guid? CategoryID = null);

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
    DateTimeOffset ReportedAt);
