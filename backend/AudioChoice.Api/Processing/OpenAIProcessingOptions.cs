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
    /// Which service the three analysis models are reached through: "openai" or "bedrock".
    /// </summary>
    /// <remarks>
    /// Separate from the model names on purpose. The tier names below say which model does
    /// which job; this says who is asked. That split is what lets a tier move to a different
    /// vendor by configuration, and it is why the checkpoint cache keys on the model name --
    /// answers from one model are never reused for another.
    /// </remarks>
    public string AnalysisProvider { get; init; } = "openai";

    /// <summary>
    /// Which AWS region Bedrock is called in. Empty means the SDK resolves it as Polly's
    /// client already does, from the environment or the instance's own configuration.
    /// </summary>
    public string BedrockRegion { get; init; } = string.Empty;

    public string AnalysisModel { get; init; } = "gpt-5.6-luna";
    public string SceneVerificationModel { get; init; } = "gpt-5.6-terra";
    public string SceneEscalationModel { get; init; } = "gpt-5.6-sol";
    public string ScannerVersion { get; init; } = "3.6";
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
