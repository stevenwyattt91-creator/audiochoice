using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Processing;

public sealed class OpenAIProcessingOptions
{
    public bool WorkerEnabled { get; init; }
    /// <summary>Either "openai" or "faster-whisper". The latter is intended for the isolated GPU worker.</summary>
    public string TranscriptionProvider { get; init; } = "openai";
    /// <summary>Local endpoint used by the faster-whisper GPU service.</summary>
    public string FasterWhisperEndpoint { get; init; } = "http://127.0.0.1:8001/";
    public int FasterWhisperTimeoutSeconds { get; init; } = 600;
    public int TranscriptionWorkers { get; init; } = 3;
    public int TranscriptionConcurrencyPerWorker { get; init; } = 2;
    public int TranscriptionMaximumRetries { get; init; } = 3;
    public int ScanWorkerConcurrency { get; init; } = 1;
    public int ContentAnalysisConcurrency { get; init; } = 3;
    public int SceneVerificationConcurrency { get; init; } = 3;
    public int SceneEscalationConcurrency { get; init; } = 2;
    public string FasterWhisperModel { get; init; } = "large-v3-turbo";
    public string FasterWhisperFallbackModel { get; init; } = "large-v3";
    /// <summary>
    /// Uses cheap local transcript cues to select the narrative windows sent to Luna.
    /// Keep disabled for established production workers until the isolated lane has
    /// completed its comparison run.
    /// </summary>
    public bool LocalCandidateFunnelEnabled { get; init; }
    /// <summary>
    /// Run the complete transcript through the Lambda-hosted high-recall cue scanner
    /// and send only its sexual-content candidate windows to Terra. This avoids using
    /// the general OpenAI model as the initial pass.
    /// </summary>
    public bool LambdaFirstPassEnabled { get; init; }
    public string ApiKey { get; init; } = string.Empty;
    public string BaseURL { get; init; } = "https://api.openai.com/v1/";
    public string TranscriptionModel { get; init; } = "whisper-1";
    /// <summary>
    /// Applies to every Luna/Terra/Sol request made through OpenAIResponsesModelClient.
    /// </summary>
    /// <remarks>
    /// Left unset this client used HttpClient's 100-second platform default, which a long scan
    /// batch (tens of thousands of input tokens, on the largest windows in a full-book Luna
    /// pass) can outlast under real load -- and a timeout throws before any HTTP status code
    /// is received, so the retry loop below it never saw the failure at all. A rescan that had
    /// already spent most of an hour on transcription and 90% of its analysis was lost to a
    /// single slow response on the home stretch, with nothing to retry against.
    /// </remarks>
    public int AnalysisRequestTimeoutSeconds { get; init; } = 300;
    /// <summary>
    /// Which service the three analysis models are reached through: "openai" or "bedrock".
    /// </summary>
    /// <remarks>
    /// Separate from the model names on purpose. The tier names below say which model does
    /// which job; this says who is asked. That split is what lets a tier move to a different
    /// vendor by configuration, and it is why the checkpoint cache keys on the model name --
    /// answers from one model are never reused for another.
    /// </remarks>
    /// <summary>
    /// The lane a scan is queued on when the request does not ask for one.
    /// </summary>
    /// <remarks>
    /// Was hardcoded to the Azure lane, which transcribes through OpenAI. That lane also
    /// cannot finish an audiobook: its deployment still carries a paid-test ceiling of 300
    /// seconds and one chunk, so anything longer than five minutes throws. Pointing the
    /// default at the GPU lane removes the last use of OpenAI from scanning and moves every
    /// scan onto a worker that can actually complete one.
    ///
    /// Configuration rather than a code change, so a lane can be redirected while a host is
    /// down without a deploy.
    /// </remarks>
    public string DefaultProcessingLane { get; init; } = ScanProcessingLanes.IOSBetaLambda;

    public string AnalysisProvider { get; init; } = "openai";

    /// <summary>
    /// Which AWS region Bedrock is called in. Empty means the SDK resolves it as Polly's
    /// client already does, from the environment or the instance's own configuration.
    /// </summary>
    public string BedrockRegion { get; init; } = string.Empty;

    public string AnalysisModel { get; init; } = "gpt-5.6-luna";
    public string SceneVerificationModel { get; init; } = "gpt-5.6-terra";
    public string SceneEscalationModel { get; init; } = "gpt-5.6-sol";

    /// <summary>
    /// The model that confirms whether proposed violence actually describes injury.
    /// </summary>
    /// <remarks>
    /// Its own setting rather than the scene verifier's. Reusing that one coupled two unrelated
    /// judgements: moving the sexual-scene stages to OpenAI silently moved violence with them,
    /// and violence verification went from rejecting 61% of proposals to confirming 237 of 237.
    /// The two tiers answer different questions and the best model for each is not the same one.
    ///
    /// Empty means fall back to the scene verifier, so a configuration that predates this setting
    /// keeps working.
    /// </remarks>
    public string ViolenceVerificationModel { get; init; } = string.Empty;

    /// <summary>
    /// The Terra confidence at or above which an accepted scene finalizes directly, skipping
    /// Sol entirely.
    /// </summary>
    /// <remarks>
    /// Previously every Terra-accepted scene, regardless of confidence, was re-paid-for at
    /// Sol -- "double-paying on the easy path" per the backend filtering checklist. Terra's
    /// own prompt already requires both evidence booleans and at least 0.85 confidence before
    /// it may accept a scene at all, so an accepted scene is never a guess; a genuinely
    /// ambiguous or borderline one is what <c>needsEscalation</c> exists to flag, and that
    /// path is untouched. Set above 0.85 -- deliberately higher than the minimum Terra's own
    /// prompt already enforces for "accepted" -- so only Terra's clearest calls skip the
    /// second pass; anything less certain, including an accepted scene that only barely
    /// cleared 0.85, still gets Sol's review.
    /// </remarks>
    public double SolEscalationConfidenceThreshold { get; init; } = .95;

    /// <summary>The violence verifier, or the scene verifier when none is set.</summary>
    public string EffectiveViolenceVerificationModel =>
        string.IsNullOrWhiteSpace(ViolenceVerificationModel)
            ? SceneVerificationModel
            : ViolenceVerificationModel;
    // Bumped for the concurrency/word-snap/sentence-boundary overhaul: word-snapped scene
    // boundaries (replacing flat second-based padding and clamps), sentence-boundary scene
    // merging (replacing the flat 45s gap), a Terra entry gate excluding lone weak
    // singletons, Sol dispatch gated to only ambiguous/low-confidence Terra results, and
    // Luna no longer proposing profanity labels. Results are stored per edition and scanner
    // version, so this writes new rows rather than overwriting results from the prior
    // pipeline.
    //
    // Bumped again to add the sexual_violence category: its own taxonomy group, its own
    // Luna definition (mutually exclusive with the consensual scene ladder), and its own
    // Terra/Sol verification lane with a consent-specific evidence requirement. A scan made
    // under the prior version never had a chance to report sexual_violence at all, so it
    // must not be presented as though it had.
    //
    // Bumped again for the sexual-content keyword safety net (AddUncoveredSexualCandidates):
    // a scan made under the prior version never ran the high-recall keyword windows against
    // Luna's own coverage gaps, so it may be missing a scene the safety net would have
    // caught. A cached scan_result from before this change must not be presented as though
    // it already had that second chance.
    //
    // Bumped again: a real listener report confirmed a confirmed complete-scene event's own
    // boundary was landing after real content had already started playing -- the refinement
    // instruction asked the verifier for "the activity or its immediate unmistakable
    // lead-in," which was read narrowly enough to skip the actual kissing/buildup that leads
    // into a scene. A scan under the prior version must not be presented as though its
    // boundaries already reflected the wider reading.
    //
    // Bumped again: an escalated sexual-content candidate's outcome is now a three-vote
    // majority (Terra plus two independent Sol calls) rather than a single Sol call
    // overriding Terra outright, guarding against the same-passage-different-verdict
    // variance a real rescan exposed on this pipeline. A scan made under the prior version
    // had only one Sol opinion behind an escalated result, not a majority.
    public string ScannerVersion { get; init; } = "5.4-sol-majority-vote";
    /// <summary>Only jobs in this lane may be claimed by this worker instance.</summary>
    public string ProcessingLane { get; init; } = ScanProcessingLanes.AzureOpenAI;
    /// <summary>
    /// The lowest confidence that may reach a listener as a filter event.
    /// </summary>
    /// <remarks>
    /// The analysis prompt already tells the model to omit anything below 0.55, but that was
    /// advisory: nothing enforced it, and no confidence threshold existed anywhere outside
    /// the sexual-scene verifier. Enforcing the number the prompt already states means a
    /// low-confidence guess cannot be presented with the same authority as a firm detection.
    ///
    /// Exact profanity word matches are exempt, because matching a literal word involves no
    /// judgement and is reported at full confidence.
    /// </remarks>
    public double MinimumEventConfidence { get; init; } = .55;
    public int MaximumRetries { get; init; } = 3;
    public int MaximumJobAttempts { get; init; } = 3;
    public int MaximumSegmentsPerAnalysisRequest { get; init; } = 100;
    public int MaximumSceneVerificationRequestsPerJob { get; init; } = 50;
    // The strict sexual-content lane escalates every plausible scene candidate.
    // Keep the overall job ceiling bounded by the existing 50-candidate safety cap.
    public int MaximumSceneEscalationRequestsPerJob { get; init; } = 50;
    public int MaximumChunksPerJob { get; init; } = 500;
    public int MaximumTranscriptSegmentsPerJob { get; init; } = 100_000;
    public double MaximumAudioDurationSeconds { get; init; } = 108_000;
}
