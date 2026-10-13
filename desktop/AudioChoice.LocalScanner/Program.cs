using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AudioChoice.Api.Contracts;
using AudioChoice.Api.Processing;
using AudioChoice.Api.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using AudioChoice.LocalScanner;

// Runs the exact same scan pipeline the Lambda/cloud worker runs (ScanPipeline,
// OpenAIContentAnalysisProvider, FfmpegAudioChunker -- all reused by project reference, see
// AudioChoice.LocalScanner.csproj's own remarks), but reads the audiobook from a file picked by
// hand instead of a job queue, transcribes it with a local faster-whisper subprocess instead of
// the Lambda GPU host's HTTP service, and writes the result to a .audiochoicepack file instead
// of a shared Postgres database -- there is no server here at all, only this one process.
//
// Usage:
//   AudioChoiceLocalScanner.exe <audiobook-file> [--out <output-folder>]
//
// Configuration (environment variables or appsettings.local.json next to this executable):
//   AudioChoice__OpenAI__AnalysisModel            e.g. gemini-2.5-flash-lite
//   AudioChoice__OpenAI__SceneVerificationModel   e.g. gemini-2.5-flash
//   AudioChoice__OpenAI__SceneEscalationModel     e.g. deepinfra/Qwen/Qwen3.8-27B-Instruct
//   AudioChoice__OpenAI__GeminiApiKey
//   AudioChoice__OpenAI__DeepInfraApiKey
//   AudioChoice__Ffmpeg__FfmpegPath / FfprobePath  (only needed if not already on PATH)

var baseDirectory = AppContext.BaseDirectory;

var configuration = new ConfigurationBuilder()
    .SetBasePath(baseDirectory)
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables()
    .Build();

using var loggerFactory = LoggerFactory.Create(builder =>
    builder.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
    }).SetMinimumLevel(LogLevel.Information));
var logger = loggerFactory.CreateLogger("AudioChoiceLocalScanner");

string? audioFilePath = null;
string outputFolder = Path.Combine(baseDirectory, "scans");
for (var index = 0; index < args.Length; index += 1)
{
    if (args[index] is "--out" or "-o")
    {
        if (index + 1 >= args.Length)
        {
            Console.Error.WriteLine("--out requires a folder path.");
            return 1;
        }
        outputFolder = args[++index];
    }
    else if (!args[index].StartsWith('-'))
    {
        audioFilePath = args[index];
    }
}

if (audioFilePath is null)
{
    Console.WriteLine("""
        AudioChoice Local Scanner

        Scans an audiobook file for sexual content and produces a .audiochoicepack
        file ready to import on your phone -- no cloud scanning required.

        Usage:
          AudioChoiceLocalScanner.exe <audiobook-file> [--out <output-folder>]
        """);
    return audioFilePath is null && args.Length == 0 ? 1 : 0;
}

if (!File.Exists(audioFilePath))
{
    Console.Error.WriteLine($"File not found: {audioFilePath}");
    return 1;
}

Directory.CreateDirectory(outputFolder);

try
{
    var openAIOptions = configuration.GetSection("AudioChoice:OpenAI").Get<OpenAIProcessingOptions>()
        ?? new OpenAIProcessingOptions();
    var ffmpegOptions = configuration.GetSection("AudioChoice:Ffmpeg").Get<FfmpegAudioChunkerOptions>()
        ?? new FfmpegAudioChunkerOptions();
    // Defaults to the virtual environment setup.ps1 creates right next to this executable,
    // so a fresh install needs no configuration at all beyond an API key -- only an unusual
    // setup (a different venv location, a system Python someone prefers) needs to override
    // AudioChoice:Whisper:PythonExecutable.
    var defaultVenvPython = Path.Combine(baseDirectory, "whisper-venv", "Scripts", "python.exe");
    var pythonExecutable = configuration["AudioChoice:Whisper:PythonExecutable"]
        ?? (File.Exists(defaultVenvPython) ? defaultVenvPython : "python");
    var whisperModel = configuration["AudioChoice:Whisper:Model"] ?? "large-v3-turbo";
    var transcribeScript = Path.Combine(baseDirectory, "python", "transcribe.py");

    logger.LogInformation("Fingerprinting {File}...", Path.GetFileName(audioFilePath));
    var fingerprint = await ComputeFingerprint(audioFilePath, logger);

    logger.LogInformation(
        "Analysis tiers: first pass {AnalysisModel}; scene verification " +
        "{SceneVerificationModel}; escalation {SceneEscalationModel}",
        openAIOptions.AnalysisModel, openAIOptions.SceneVerificationModel,
        openAIOptions.SceneEscalationModel);

    var processRunner = new SystemProcessRunner();
    var chunker = new FfmpegAudioChunker(processRunner, ffmpegOptions);
    var transcription = new LocalWhisperTranscriptionProvider(
        pythonExecutable, transcribeScript, whisperModel,
        loggerFactory.CreateLogger<LocalWhisperTranscriptionProvider>());

    var dataPaths = new AudioChoiceDataPaths(
        new MinimalHostEnvironment(baseDirectory), configuration);
    dataPaths.EnsureDirectories();

    IAnalysisModelClient modelClient = new RoutingAnalysisModelClient(
        bedrock: null,
        openAI: string.IsNullOrWhiteSpace(openAIOptions.ApiKey)
            ? null
            : new OpenAIResponsesModelClient(
                CreateHttpClient(openAIOptions.BaseURL, openAIOptions.AnalysisRequestTimeoutSeconds),
                openAIOptions,
                loggerFactory.CreateLogger<OpenAIResponsesModelClient>()),
        vllm: string.IsNullOrWhiteSpace(openAIOptions.VllmEndpoint)
            ? null
            : new VllmModelClient(
                CreateHttpClient(openAIOptions.VllmEndpoint, openAIOptions.AnalysisRequestTimeoutSeconds),
                openAIOptions,
                loggerFactory.CreateLogger<VllmModelClient>()),
        gemini: string.IsNullOrWhiteSpace(openAIOptions.GeminiApiKey)
            ? null
            : new GeminiModelClient(
                CreateHttpClient(openAIOptions.GeminiBaseURL, openAIOptions.AnalysisRequestTimeoutSeconds),
                openAIOptions,
                loggerFactory.CreateLogger<GeminiModelClient>()),
        deepInfra: string.IsNullOrWhiteSpace(openAIOptions.DeepInfraApiKey)
            ? null
            : new DeepInfraModelClient(
                CreateHttpClient(openAIOptions.DeepInfraBaseURL, openAIOptions.AnalysisRequestTimeoutSeconds),
                openAIOptions,
                loggerFactory.CreateLogger<DeepInfraModelClient>()),
        loggerFactory.CreateLogger<RoutingAnalysisModelClient>());

    var analysisProvider = new OpenAIContentAnalysisProvider(
        modelClient, openAIOptions, dataPaths,
        loggerFactory.CreateLogger<OpenAIContentAnalysisProvider>());

    // No transcript store: this app has no persistent catalog to resume a transcript from
    // across runs, and does not need one -- a single scan runs start to finish in one
    // process, and if it is interrupted, running it again is the recovery path (see the
    // original planning conversation's own note that one-at-a-time, re-run-if-interrupted is
    // an acceptable tradeoff for this use case).
    var transcriptStore = new NullPrivateTranscriptStore();

    // FfmpegAudioChunker also implements IPreMaterializedAudioChunker, and ScanPipeline
    // always requires a ConcurrentChunkTranscriber on that path (see ScanPipeline.cs's own
    // ProcessMaterialized) -- there is no code path that skips it, even for a single-chunk
    // book, so this is required rather than optional here.
    var concurrentTranscriber = new ConcurrentChunkTranscriber(
        transcription, openAIOptions,
        loggerFactory.CreateLogger<ConcurrentChunkTranscriber>());

    var pipeline = new ScanPipeline(
        chunker, transcription, analysisProvider, transcriptStore, openAIOptions,
        concurrentTranscriber,
        loggerFactory.CreateLogger<ScanPipeline>());

    var upload = new UploadRecord(
        ID: Guid.NewGuid(),
        OwnerUserID: Guid.Empty,
        Fingerprint: fingerprint,
        FileName: Path.GetFileName(audioFilePath),
        ContentType: "application/octet-stream",
        FileSize: fingerprint.FileSize,
        TokenHash: string.Empty,
        ExpiresAt: DateTimeOffset.UtcNow.AddDays(1),
        IsUploaded: true,
        StoredPath: audioFilePath);

    logger.LogInformation("Starting scan. This can take a while for a long book.");
    var started = DateTimeOffset.UtcNow;
    var result = await pipeline.Process(
        upload,
        reportProgress: (percent, stage) => logger.LogInformation("  {Percent}% ({Stage})", percent, stage),
        cancellationToken: CancellationToken.None,
        reportChunkProgress: (completed, total) =>
            logger.LogInformation("  chunk {Completed}/{Total}", completed, total));
    var elapsed = DateTimeOffset.UtcNow - started;

    logger.LogInformation(
        "Scan complete in {Elapsed}: {EventCount} event(s) found.",
        elapsed, result.Events.Count);

    var packPath = await LocalScanPack.Write(
        outputFolder, audioFilePath, fingerprint, result, analysisProvider.ScannerVersion);
    logger.LogInformation("Wrote {PackPath}", packPath);
    logger.LogInformation(
        "Copy that file to your phone (AirDrop, Google Drive, a cable -- whatever is easiest) " +
        "and open it with AudioChoice to import.");
    return 0;
}
catch (Exception error)
{
    logger.LogError(error, "Scan failed.");
    return 1;
}

static HttpClient CreateHttpClient(string baseUrl, int timeoutSeconds) => new()
{
    BaseAddress = new Uri(baseUrl),
    Timeout = TimeSpan.FromSeconds(Math.Max(30, timeoutSeconds)),
};

static async Task<BookFingerprint> ComputeFingerprint(string filePath, ILogger logger)
{
    // Must match exactly what the mobile clients compute for the same file (see
    // CloudContracts.cs's own remarks on BookFingerprint): a SHA-256 over the whole file as
    // uppercase hex, paired with the file's real byte length, fingerprint version 1. Get this
    // wrong and the phone that receives this pack's manifest derives a different key and
    // cannot find a matching book to attach the scan result to.
    using var sha256 = SHA256.Create();
    await using var stream = File.OpenRead(filePath);
    var hashBytes = await sha256.ComputeHashAsync(stream);
    var hex = Convert.ToHexString(hashBytes); // Convert.ToHexString is already uppercase.
    var fileInfo = new FileInfo(filePath);
    var fileType = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
    return new BookFingerprint(
        Version: 1,
        Sha256: hex,
        FileSize: fileInfo.Length,
        Duration: null,
        FileType: fileType,
        WorkTitle: null,
        Author: null,
        SeriesTitle: null,
        SeriesNumber: null,
        EditionType: null,
        PartNumber: null,
        TotalParts: null);
}

/// <summary>
/// A transcript store that never has anything saved. See Program.cs's own remarks on why this
/// app has no persistent transcript cache to resume from.
/// </summary>
internal sealed class NullPrivateTranscriptStore : IPrivateTranscriptStore
{
    public Task<PrivateTranscript?> Load(BookFingerprint fingerprint, CancellationToken cancellationToken) =>
        Task.FromResult<PrivateTranscript?>(null);

    public Task Save(BookFingerprint fingerprint, PrivateTranscript transcript, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

/// <summary>
/// Builds the combined .audiochoicepack file a phone imports: the original audio file, the
/// scan result in the same shape the server would have returned it, and a small manifest the
/// phone reads first to decide what book this is before touching the (potentially very large)
/// audio entry.
/// </summary>
internal static class LocalScanPack
{
    public static async Task<string> Write(
        string outputFolder,
        string audioFilePath,
        BookFingerprint fingerprint,
        ScanResult result,
        string scannerVersion)
    {
        var baseName = Path.GetFileNameWithoutExtension(audioFilePath);
        var packPath = Path.Combine(outputFolder, $"{baseName}.audiochoicepack");
        if (File.Exists(packPath)) File.Delete(packPath);

        using var archive = ZipFile.Open(packPath, ZipArchiveMode.Create);

        var audioEntryName = "audio" + Path.GetExtension(audioFilePath).ToLowerInvariant();
        archive.CreateEntryFromFile(audioFilePath, audioEntryName, CompressionLevel.NoCompression);

        var scanJson = JsonSerializer.Serialize(result, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        WriteTextEntry(archive, "scan.json", scanJson);

        var manifest = new PackManifest(
            FormatVersion: 1,
            Fingerprint: fingerprint,
            AudioEntryName: audioEntryName,
            ScannerVersion: scannerVersion,
            ScannedAt: result.ScanDate,
            EventCount: result.Events.Count);
        var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        WriteTextEntry(archive, "manifest.json", manifestJson);

        return await Task.FromResult(packPath);
    }

    private static void WriteTextEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    /// <summary>
    /// What a phone reads first when it opens a .audiochoicepack, before touching the audio
    /// entry. <see cref="Fingerprint"/> is exactly the fingerprint the phone would have
    /// computed itself from the audio file, so it can match this pack to an existing library
    /// book (or hold onto the pack until that book is imported) without re-deriving it, and
    /// as a sanity check that the audio entry actually hashes to what this manifest claims.
    /// </summary>
    private sealed record PackManifest(
        int FormatVersion,
        BookFingerprint Fingerprint,
        string AudioEntryName,
        string ScannerVersion,
        DateTimeOffset ScannedAt,
        int EventCount);
}
