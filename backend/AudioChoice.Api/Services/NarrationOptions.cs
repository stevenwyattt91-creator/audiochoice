namespace AudioChoice.Api.Services;

/// <summary>
/// Server-side switches for EPUB narration.
/// </summary>
/// <remarks>
/// Off by default, so deploying this code changes nothing about a running environment until
/// somebody turns it on. The Android side is gated separately, on the experimental build, so
/// a beta or release listener cannot reach these endpoints even where they are enabled.
/// </remarks>
public sealed class NarrationOptions
{
    /// <summary>Whether <c>POST /v1/narration/text-scans</c> is reachable at all.</summary>
    public bool TextScanEnabled { get; init; }

    /// <summary>
    /// How long a text scan may take before the request is abandoned.
    /// </summary>
    /// <remarks>
    /// A whole book is a large classification job, and the client's import screen cannot
    /// wait indefinitely. Long enough for a novel at the batch sizes the analysis provider
    /// uses, and short enough that a stuck request surfaces as a failure the listener can
    /// retry rather than as an import that never finishes.
    ///
    /// Deliberately below the Android client's 90-second read timeout. If the budget were
    /// the more generous two minutes, every slow scan would reach the client as a dropped
    /// connection reading "AudioChoice could not reach the private scan service" -- which
    /// sends the listener to check their network while the server is working perfectly well
    /// -- instead of as the 504 this endpoint returns and the client can describe honestly.
    /// A server-side budget is only useful if the caller is still listening when it expires.
    /// </remarks>
    public int TextScanTimeoutSeconds { get; init; } = 75;

    /// <summary>Whether the premium synthesis endpoints are reachable.</summary>
    /// <remarks>
    /// Separate from <see cref="TextScanEnabled"/>: filtering a book's text and speaking it are
    /// different capabilities with different costs, and a server may reasonably do one and not
    /// the other.
    /// </remarks>
    public bool SynthesisEnabled { get; init; }

    /// <summary>Which provider premium synthesis is routed to first.</summary>
    public string SynthesisProvider { get; init; } = "polly";

    /// <summary>The AudioChoice-operated synthesis endpoint, when one is deployed.</summary>
    /// <remarks>
    /// Checked against the transcription endpoint at startup: the two must never resolve to the
    /// same host, or narration synthesis would compete with audiobook scanning for one GPU.
    /// </remarks>
    public string SynthesisEndpoint { get; init; } = string.Empty;

    /// <summary>
    /// Whether the primary endpoint's spend has been confirmed to land against the intended
    /// account.
    /// </summary>
    /// <remarks>
    /// False by default, which routes every chapter to the fallback. Until somebody has checked
    /// that the primary's cost appears where it is meant to, sending work there would be running
    /// up an unknown bill -- so the provider whose cost is understood is the one in effect, and
    /// it takes a recorded verification to change that.
    /// </remarks>
    public bool BillingCoverageVerified { get; init; }

    /// <summary>
    /// Measured provisioning delay for a scaled-to-zero primary endpoint, in seconds.
    /// </summary>
    /// <remarks>
    /// Added to the per-chapter budget so a cold endpoint is not abandoned while it is starting.
    /// Zero by default because it is a measurement, not an estimate: the intended source is a
    /// `cold_start_delay` row in `narration_measurements`, and a guess here would either abandon
    /// an endpoint that was about to answer or make every listener wait out a number nobody
    /// checked.
    /// </remarks>
    public int ColdStartDelaySeconds { get; init; }
}
