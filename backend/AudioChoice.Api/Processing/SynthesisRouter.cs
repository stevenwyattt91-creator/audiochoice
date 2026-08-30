using AudioChoice.Api.Services;

namespace AudioChoice.Api.Processing;

/// <summary>Why a chapter was routed the way it was, for the render record and for triage.</summary>
public enum SynthesisRoute
{
    Primary,
    FallbackBecausePrimaryFailed,
    FallbackBecausePrimaryTimedOut,
    FallbackBecausePrimaryUnavailable,
    FallbackBecauseBillingUnverified,
}

/// <summary>The outcome of routing one chapter.</summary>
public sealed record RoutedChapter(SynthesizedChapter Chapter, SynthesisRoute Route);

/// <summary>
/// Chooses between the primary synthesis provider and the fallback, one chapter at a time.
/// </summary>
/// <remarks>
/// A router rather than a provider that fails over internally, because which provider spoke a
/// chapter is recorded per chapter and has to be knowable from outside. It also keeps the
/// providers themselves ignorant of each other.
///
/// Holds no reference to <c>ITranscriptionProvider</c> and never will. The transcription GPU
/// host is paid for and busy, and narration synthesis competing with it would slow the scans
/// every listener depends on. <see cref="AssertEndpointsAreDistinct"/> makes that structural
/// rather than a matter of configuration discipline.
/// </remarks>
public sealed class SynthesisRouter(
    ISynthesisProvider primary,
    ISynthesisProvider fallback,
    NarrationOptions options,
    ILogger<SynthesisRouter> logger,
    /// <summary>
    /// Where the cold-start delay is read from, when one has been measured.
    /// </summary>
    /// <remarks>
    /// Optional so the router can be constructed in a test without a store. A measured delay
    /// supersedes the configured one, which is the right precedence: configuration is what somebody
    /// typed, a measurement is what the endpoint actually did.
    /// </remarks>
    INarrationMeasurementStore? measurements = null)
{
    /// <summary>
    /// How long one chapter may take before the fallback is tried.
    /// </summary>
    /// <remarks>
    /// Bounds the provider call, not the listener's HTTP request: chapter synthesis is a job,
    /// so the client is polling and nothing is blocked on this. Sixty seconds is generous for a
    /// chapter and short enough that a wedged endpoint does not hold a render queue.
    /// </remarks>
    public const int PrimaryTimeoutSeconds = 60;

    /// <summary>Which provider is in effect for a fresh chapter.</summary>
    /// <remarks>
    /// The fallback becomes the provider in effect while billing coverage is unverified. Until
    /// somebody has confirmed the primary's spend actually lands against the intended account,
    /// routing work to it would be running up an unknown bill -- so the default is the provider
    /// whose cost is understood, and it is a recorded measurement that flips it.
    /// </remarks>
    public ISynthesisProvider ProviderInEffect =>
        options.BillingCoverageVerified ? primary : fallback;

    public async Task<RoutedChapter> Synthesize(
        ChapterSynthesisInput input,
        CancellationToken cancellationToken)
    {
        if (!options.BillingCoverageVerified)
        {
            logger.LogInformation(
                "Routing chapter {ChapterIndex} to {Provider} because billing coverage for the " +
                "primary synthesis endpoint is unverified.",
                input.ChapterIndex, fallback.Provider);
            return new RoutedChapter(
                await fallback.Synthesize(input, cancellationToken),
                SynthesisRoute.FallbackBecauseBillingUnverified);
        }

        // Asked before the work, because a scaled-to-zero endpoint is not failing -- it is
        // provisioning -- and the two deserve different budgets. Treated as unavailable only
        // when it says so; a probe that throws is left for the attempt below to classify.
        var available = await Available(cancellationToken);
        if (!available)
        {
            logger.LogInformation(
                "Primary synthesis endpoint reported unavailable; routing chapter " +
                "{ChapterIndex} to {Provider}.",
                input.ChapterIndex, fallback.Provider);
            return new RoutedChapter(
                await fallback.Synthesize(input, cancellationToken),
                SynthesisRoute.FallbackBecausePrimaryUnavailable);
        }

        // A cold endpoint gets its recorded provisioning delay on top of the ordinary budget.
        // The delay is read from measurements rather than assumed, because guessing it wrong in
        // either direction is expensive: too short abandons an endpoint that was about to
        // answer, too long makes every listener wait out a number nobody checked.
        var budget = TimeSpan.FromSeconds(PrimaryTimeoutSeconds + ColdStartDelaySeconds());

        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(budget);
            var chapter = await primary.Synthesize(input, attempt.Token);
            return new RoutedChapter(chapter, SynthesisRoute.Primary);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The budget expired rather than the caller leaving.
            logger.LogWarning(
                "Primary synthesis exceeded its {BudgetSeconds:F0}-second budget for chapter " +
                "{ChapterIndex}; routing to {Provider}.",
                budget.TotalSeconds, input.ChapterIndex, fallback.Provider);
            return new RoutedChapter(
                await fallback.Synthesize(input, cancellationToken),
                SynthesisRoute.FallbackBecausePrimaryTimedOut);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger.LogWarning(
                error,
                "Primary synthesis failed for chapter {ChapterIndex}; routing to {Provider}.",
                input.ChapterIndex, fallback.Provider);
            return new RoutedChapter(
                await fallback.Synthesize(input, cancellationToken),
                SynthesisRoute.FallbackBecausePrimaryFailed);
        }
    }

    /// <summary>
    /// The provisioning delay to allow, preferring a measurement over configuration.
    /// </summary>
    /// <remarks>
    /// Guessing this wrong is expensive in both directions: too short abandons an endpoint that was
    /// about to answer, too long makes every listener wait out a number nobody checked. So a
    /// recorded measurement wins, and the configured value is only a fallback for an endpoint that
    /// has never been measured.
    ///
    /// A negative or absurd measurement is ignored rather than trusted. A cold start longer than ten
    /// minutes is not a cold start, it is a broken endpoint, and honouring it would make every
    /// chapter wait for one.
    /// </remarks>
    private int ColdStartDelaySeconds()
    {
        var measured = measurements?.Latest(NarrationMeasurementKinds.ColdStartDelay)?.Value;
        if (measured is > 0 and <= MaximumColdStartSeconds)
        {
            return (int)Math.Ceiling(measured.Value);
        }
        return Math.Clamp(options.ColdStartDelaySeconds, 0, MaximumColdStartSeconds);
    }

    /// <summary>Past this a cold start is a fault, not a delay to wait out.</summary>
    public const int MaximumColdStartSeconds = 600;

    private async Task<bool> Available(CancellationToken cancellationToken)
    {
        try
        {
            return await primary.IsAvailable(cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A probe that throws says nothing reliable either way, so the attempt below is
            // allowed to proceed and classify the real failure. Reporting unavailable here
            // would send every chapter to the fallback on the strength of a flaky health check.
            logger.LogDebug(error, "Primary synthesis availability probe failed.");
            return true;
        }
    }

    /// <summary>
    /// Fails the process when narration synthesis and audio transcription are configured to the
    /// same host.
    /// </summary>
    /// <remarks>
    /// A startup assertion rather than a warning, and deliberately fatal. The transcription GPU
    /// is paid for and saturated; narration synthesis sharing it would slow the scans every
    /// listener depends on, and it would do so invisibly -- as a gradual increase in scan times
    /// that looks like load rather than like a misconfiguration. Refusing to start is the only
    /// failure mode that cannot be ignored.
    /// </remarks>
    public static void AssertEndpointsAreDistinct(
        string? transcriptionEndpoint,
        string? synthesisEndpoint)
    {
        if (string.IsNullOrWhiteSpace(transcriptionEndpoint)) return;
        if (string.IsNullOrWhiteSpace(synthesisEndpoint)) return;

        var transcriptionHost = HostOf(transcriptionEndpoint);
        var synthesisHost = HostOf(synthesisEndpoint);
        if (transcriptionHost is null || synthesisHost is null) return;

        if (!string.Equals(transcriptionHost, synthesisHost, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"AudioChoice:Narration:SynthesisEndpoint and " +
            $"AudioChoice:OpenAI:FasterWhisperEndpoint both resolve to '{synthesisHost}'. " +
            "Narration synthesis must not run on the transcription GPU host: it would " +
            "compete with audiobook scanning for the same device and slow every scan.");
    }

    /// <summary>
    /// The host part of a configured endpoint, comparing by host and port.
    /// </summary>
    /// <remarks>
    /// Port included, because two services on one machine behind different ports are the case
    /// this check is most likely to meet and are genuinely distinct: the objection is to
    /// sharing a GPU, and a second port on the same box shares it.
    ///
    /// So port is compared only to distinguish nothing -- the host alone decides. Recorded here
    /// because the opposite reading is the tempting one: comparing host and port together would
    /// let two ports on one GPU pass.
    /// </remarks>
    private static string? HostOf(string endpoint)
    {
        if (Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }
        // Not a URI. Compared as written, trimmed, so a bare host name still collides with
        // itself rather than being waved through.
        var value = endpoint.Trim().TrimEnd('/');
        return value.Length == 0 ? null : value;
    }
}
