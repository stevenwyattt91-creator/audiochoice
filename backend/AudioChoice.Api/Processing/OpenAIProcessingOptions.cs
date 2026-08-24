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
    public string FasterWhisperModel { get; init; } = "large-v3-turbo";
    public string FasterWhisperFallbackModel { get; init; } = "large-v3";
    /// <summary>
    /// Uses cheap local transcript cues to select the narrative windows sent to Luna.
    /// Keep disabled for established production workers until the isolated lane has
    /// completed its comparison run.
    /// </summary>
    public bool LocalCandidateFunnelEnabled { get; init; }
    public string ApiKey { get; init; } = string.Empty;
    public string BaseURL { get; init; } = "https://api.openai.com/v1/";
    public string TranscriptionModel { get; init; } = "whisper-1";
    public string AnalysisModel { get; init; } = "gpt-5.6-luna";
    public string SceneVerificationModel { get; init; } = "gpt-5.6-terra";
    public string SceneEscalationModel { get; init; } = "gpt-5.6-sol";
    public string ScannerVersion { get; init; } = "3.2";
    /// <summary>Only jobs in this lane may be claimed by this worker instance.</summary>
    public string ProcessingLane { get; init; } = ScanProcessingLanes.AzureOpenAI;
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
