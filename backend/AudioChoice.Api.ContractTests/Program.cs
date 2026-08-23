using AudioChoice.Api.Contracts;
using AudioChoice.Api.Processing;
using AudioChoice.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

var googleAuth = new ExternalAuthOptions
{
    GoogleClientID = "android-web-client, ios-client,android-web-client"
};
Assert(
    googleAuth.GoogleClientIDs.SequenceEqual(["android-web-client", "ios-client"]),
    "Multiple Google token audiences were not parsed and deduplicated.");

var catalog = new InMemoryScanCatalog();
var fingerprint = new BookFingerprint(
    1,
    "ABC123",
    4,
    60,
    "m4b",
    "Test Book",
    "AudioChoice",
    null,
    null,
    "standard",
    null,
    null);

var result = new ScanResult(
    Array.Empty<ScanEvent>(),
    DateTimeOffset.UtcNow,
    "1.0");

catalog.SaveResult(fingerprint, result);
Assert(catalog.FindResult(fingerprint) == result, "Fingerprint lookup failed.");
Assert(catalog.RecoverableJobs().Count == 0, "Completed-only catalog reported recoverable jobs.");

var fourthWingPart1 = new BookFingerprint(
    1,
    "3d37a3c485debd42249bc939deed657505d18c939bd43c00dae99e10800916e",
    449954471,
    28800,
    "m4a",
    "Fourth Wing",
    "Rebecca Yarros",
    "The Empyrean",
    1,
    "Dramatized Adaptation",
    null,
    null);
Assert(
    EditionTitleFormatter.Format(fourthWingPart1) == "Fourth Wing (Part 1 of 2) (Dramatized Adaptation)",
    "Fourth Wing Part 1 fallback title was not canonicalized.");

var persistenceFolder = Path.Combine(
    Path.GetTempPath(),
    $"audiochoice-catalog-{Guid.NewGuid()}");
Directory.CreateDirectory(persistenceFolder);
try
{
    var persistencePath = Path.Combine(persistenceFolder, "catalog.json");
    new InMemoryScanCatalog(persistencePath).SaveResult(fingerprint, result);
    var reloadedCatalog = new InMemoryScanCatalog(persistencePath);
    Assert(
        reloadedCatalog.FindResult(fingerprint)?.ScannerVersion == result.ScannerVersion,
        "Persistent scan result did not survive catalog reload.");
}
finally
{
    Directory.Delete(persistenceFolder, true);
}

var accountFolder = Path.Combine(Path.GetTempPath(), $"audiochoice-accounts-{Guid.NewGuid()}");
try
{
    var accountPath = Path.Combine(accountFolder, "accounts.json");
    var accounts = new FileAccountStore(accountPath);
    var registration = accounts.Register(new RegisterRequest("reader@example.com", "correct-horse-battery-staple", "Reader"));
    Assert(registration is not null, "Email registration failed.");
    Assert(accounts.Authenticate(registration!.Response.AccessToken)?.Email == "reader@example.com", "Session authentication failed.");
    Assert(accounts.VerifyEmail(registration.Verification.Token), "Email verification failed.");
    Assert(!accounts.VerifyEmail(registration.Verification.Token), "Email verification token was reusable.");
    Assert(accounts.Login(new LoginRequest("reader@example.com", "wrong-password-value")) is null, "Incorrect password was accepted.");
    Assert(new FileAccountStore(accountPath).Login(new LoginRequest("reader@example.com", "correct-horse-battery-staple")) is not null, "Account did not survive reload.");
    var reset = accounts.CreatePasswordReset("reader@example.com");
    Assert(reset is not null, "Password reset token was not created.");
    Assert(accounts.ResetPassword(reset!.Token, "new-correct-horse-battery-staple"), "Password reset failed.");
    Assert(!accounts.ResetPassword(reset.Token, "another-correct-horse-battery-staple"), "Password reset token was reusable.");
    Assert(accounts.Authenticate(registration.Response.AccessToken) is null, "Password reset did not invalidate the old session.");
    Assert(accounts.Login(new LoginRequest("reader@example.com", "correct-horse-battery-staple")) is null, "Old password remained valid.");
    var newSession = accounts.Login(new LoginRequest("reader@example.com", "new-correct-horse-battery-staple"));
    Assert(newSession is not null, "New password was not accepted.");
    accounts.Logout(newSession!.AccessToken);
    Assert(accounts.Authenticate(newSession.AccessToken) is null, "Logged-out session remained active.");
}
finally
{
    if (Directory.Exists(accountFolder)) Directory.Delete(accountFolder, true);
}

var libraryFolder = Path.Combine(Path.GetTempPath(), $"audiochoice-library-{Guid.NewGuid()}");
try
{
    var libraryPath = Path.Combine(libraryFolder, "library.json");
    var library = new FileUserLibraryStore(libraryPath);
    var firstUser = Guid.NewGuid();
    var secondUser = Guid.NewGuid();
    var book = library.Upsert(firstUser, new LibraryBookUpsertRequest(
        fingerprint, "Test Book", "AudioChoice", "Narrator", null));
    Assert(library.List(firstUser).Single().ID == book.ID, "Library book was not stored.");
    Assert(library.List(secondUser).Count == 0, "Library data leaked to another user.");

    var progressed = library.UpdateProgress(
        firstUser, book.ID, new PlaybackProgressRequest(123.5, false));
    Assert(progressed?.PlaybackPositionSeconds == 123.5, "Playback progress was not saved.");
    Assert(library.UpdateProgress(
        secondUser, book.ID, new PlaybackProgressRequest(999, false)) is null,
        "Another user changed playback progress.");

    var bookmark = library.AddBookmark(
        firstUser, book.ID, new BookmarkCreateRequest(120, "Important", "A note"));
    Assert(bookmark is not null, "Bookmark was not saved.");
    Assert(library.ListBookmarks(secondUser, book.ID) is null, "Bookmarks leaked to another user.");
    Assert(!library.DeleteBookmark(secondUser, bookmark!.ID), "Another user deleted a bookmark.");
    Assert(new FileUserLibraryStore(libraryPath).List(firstUser).Single()
        .PlaybackPositionSeconds == 123.5, "Library state did not survive reload.");
}
finally
{
    if (Directory.Exists(libraryFolder)) Directory.Delete(libraryFolder, true);
}

var authorizationRequest = new CloudUploadAuthorizationRequest(
    fingerprint,
    "test.m4b",
    "audio/mp4",
    4);

var scanOwner = Guid.NewGuid();
var upload = catalog.CreateUpload(
    scanOwner,
    authorizationRequest,
    DateTimeOffset.UtcNow.AddMinutes(15),
    "token");

var expiredUpload = catalog.CreateUpload(
    scanOwner,
    authorizationRequest,
    DateTimeOffset.UtcNow.AddMinutes(-1),
    "expired-token");
Assert(
    catalog.ExpiredUploads(DateTimeOffset.UtcNow).Any(value => value.ID == expiredUpload.ID),
    "Expired upload was not offered for cleanup.");
Assert(catalog.MarkUploadDeleted(expiredUpload.ID), "Expired upload was not marked deleted.");

Assert(
    catalog.CreateJob(scanOwner, upload.ID, fingerprint) is null,
    "An incomplete upload created a scan job.");

Assert(
    catalog.MarkUploaded(upload.ID, "/private/test.audio"),
    "Upload completion was not recorded.");

Assert(
    catalog.CreateJob(Guid.NewGuid(), upload.ID, fingerprint) is null,
    "Another user created a scan job for an upload they do not own.");

var job = catalog.CreateJob(scanOwner, upload.ID, fingerprint);
Assert(job?.Status == CloudScanStatus.Queued, "Queued job creation failed.");
Assert(catalog.FindJob(job!.ID) == job, "Job lookup failed.");
Assert(catalog.CanAccessJob(job.ID, scanOwner), "Scan owner could not access the job.");

var follower = Guid.NewGuid();
var followerUpload = catalog.CreateUpload(
    follower, authorizationRequest, DateTimeOffset.UtcNow.AddMinutes(15), "follower-token");
Assert(catalog.MarkUploaded(followerUpload.ID, "/private/follower.audio"),
    "Follower upload completion was not recorded.");
var sharedJob = catalog.CreateJob(follower, followerUpload.ID, fingerprint);
Assert(sharedJob?.ID == job.ID, "Duplicate work was created for the same fingerprint.");
Assert(catalog.CanAccessJob(job.ID, follower), "Scan follower could not access the shared job.");
Assert(!catalog.CanAccessJob(job.ID, Guid.NewGuid()), "Unrelated user could access the scan job.");

Assert(
    catalog.SetJobStatus(job.ID, CloudScanStatus.Processing),
    "Processing status was not saved.");

Assert(
    catalog.CompleteJob(job.ID, result),
    "Completed scan result was not saved.");

Assert(
    catalog.FindJob(job.ID)?.Status == CloudScanStatus.Completed,
    "Completed job status lookup failed.");

var queue = new ScanJobQueue();
Assert(queue.TryQueue(job.ID), "Scan job was not queued.");
Assert(!queue.TryQueue(job.ID), "Duplicate scan job was queued.");
Assert(await queue.Dequeue(CancellationToken.None) == job.ID, "Wrong scan job dequeued.");
queue.Renew(job.ID);
queue.Complete(job.ID);

var transcriptStore = new CapturingTranscriptStore();
var pipeline = new ScanPipeline(
    new FakeAudioChunker(),
    new FakeTranscriptionProvider(),
    new FakeAnalysisProvider(),
    transcriptStore,
    new OpenAIProcessingOptions());

var pipelineResult = await pipeline.Process(
    upload with { IsUploaded = true, StoredPath = "/private/test.audio" },
    null,
    CancellationToken.None);

Assert(
    transcriptStore.Transcript?.Segments.Single().StartTime == 10,
    "Chunk timestamp offset was not applied.");
Assert(
    transcriptStore.SaveCount == 2 && transcriptStore.Transcript?.IsComplete == true,
    "Partial and completed transcript checkpoints were not saved.");
Assert(
    pipelineResult.ScannerVersion == "contract-test",
    "Pipeline scanner version was not returned.");

var temporaryAudio = Path.GetTempFileName();
var chunkPaths = new List<string>();
var chunks = new List<AudioChunk>();

try
{
    var chunker = new FfmpegAudioChunker(
        new FakeProcessRunner(1201),
        new FfmpegAudioChunkerOptions
        {
            ChunkDurationSeconds = 600,
            OverlapSeconds = 2
        });

    await foreach (var chunk in chunker.CreateChunks(
        temporaryAudio,
        CancellationToken.None))
    {
        Assert(File.Exists(chunk.FilePath), "Chunk was deleted before consumption.");
        chunks.Add(chunk);
        chunkPaths.Add(chunk.FilePath);
    }
}
finally
{
    File.Delete(temporaryAudio);
}

Assert(chunks.Count == 3, "FFmpeg chunk count was incorrect.");
Assert(chunks[1].StartTime == 598, "Chunk overlap was not applied.");
Assert(chunks[2].EndTime == 1201, "Final chunk exceeded audiobook duration.");
Assert(chunkPaths.All(path => !File.Exists(path)), "Temporary chunks were not deleted.");

Assert(
    ContentTaxonomy.Mappings["profanity"].EventID ==
        Guid.Parse("21100000-0000-0000-0000-000000000001"),
    "Backend taxonomy IDs changed unexpectedly.");

var completeSceneMapping = ContentTaxonomy.Mappings["sexual_complete_scene"];
var joinedSceneEvents = SceneEventPostProcessor.Process(
    [
        new ScanEvent(Guid.NewGuid(), 100, 180, completeSceneMapping.CategoryID,
            completeSceneMapping.GroupID, completeSceneMapping.EventID, .91, "scene-a"),
        new ScanEvent(Guid.NewGuid(), 165, 260, completeSceneMapping.CategoryID,
            completeSceneMapping.GroupID, completeSceneMapping.EventID, .95, "scene-b")
    ],
    [new TranscriptSegment(0, 600, "Test transcript")]);
Assert(joinedSceneEvents.Count == 1, "Overlapping sexual scene ranges were not joined.");
Assert(joinedSceneEvents[0].StartTime == 92 && joinedSceneEvents[0].EndTime == 268,
    "Sexual scene safety padding was not applied.");
Assert(joinedSceneEvents[0].SafeDescription == "Sustained sexual activity",
    "Sexual scene safe description fallback was not applied.");

var narrowSceneEvents = SceneEventPostProcessor.Process(
    [
        new ScanEvent(Guid.NewGuid(), 300, 325, completeSceneMapping.CategoryID,
            completeSceneMapping.GroupID, completeSceneMapping.EventID, .94, "narrow-scene",
            "Brief explicit activity")
    ],
    [new TranscriptSegment(0, 600, "Test transcript")]);
Assert(narrowSceneEvents.Count == 0,
    "A short detection was incorrectly promoted to a complete-scene skip.");

var schedulerProvider = new SchedulerFakeProvider();
var scheduler = new ConcurrentChunkTranscriber(
    schedulerProvider,
    new OpenAIProcessingOptions
    {
        TranscriptionWorkers = 2,
        TranscriptionConcurrencyPerWorker = 1,
        TranscriptionMaximumRetries = 1
    },
    NullLogger<ConcurrentChunkTranscriber>.Instance);
var schedulerProgress = new System.Collections.Concurrent.ConcurrentQueue<(int Done, int Total)>();
var scheduled = await scheduler.Transcribe(
    Enumerable.Range(0, 4).Select(index => new AudioChunk($"chunk-{index}", index * 10, index * 10 + 10)).ToArray(),
    (done, total) => schedulerProgress.Enqueue((done, total)),
    CancellationToken.None);
Assert(scheduled.Select(item => item.Index).SequenceEqual([0, 1, 2, 3]),
    "Concurrent scheduler did not merge chunks by index.");
Assert(schedulerProgress.Last() == (4, 4),
    "Concurrent scheduler did not report completed/total progress.");
var retryProvider = new SchedulerFakeProvider(failFirst: true);
var retryScheduler = new ConcurrentChunkTranscriber(
    retryProvider,
    new OpenAIProcessingOptions { TranscriptionWorkers = 1, TranscriptionConcurrencyPerWorker = 1, TranscriptionMaximumRetries = 2 },
    NullLogger<ConcurrentChunkTranscriber>.Instance);
var retryResult = await retryScheduler.Transcribe(
    [new AudioChunk("retry", 0, 1)], null, CancellationToken.None);
Assert(retryResult[0].RetryCount == 1, "Transient transcription failure was not retried once.");

Console.WriteLine("AudioChoice backend contract tests passed.");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class FakeAudioChunker : IAudioChunker
{
    public async IAsyncEnumerable<AudioChunk> CreateChunks(
        string audioFilePath,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield return new AudioChunk("chunk", 10, 20);
    }
}

sealed class FakeTranscriptionProvider : ITranscriptionProvider
{
    public string ModelName => "fake-transcriber";

    public Task<IReadOnlyList<TranscriptSegment>> Transcribe(
        AudioChunk chunk,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TranscriptSegment>>(
            [new TranscriptSegment(0, 5, "test")]);
}

sealed class SchedulerFakeProvider : ITranscriptionProvider
{
    private readonly bool _failFirst;
    private int _calls;
    public SchedulerFakeProvider(bool failFirst = false) => _failFirst = failFirst;
    public string ModelName => "scheduler-test-model";
    public async Task<IReadOnlyList<TranscriptSegment>> Transcribe(AudioChunk chunk, CancellationToken cancellationToken)
    {
        await Task.Delay(10, cancellationToken);
        if (_failFirst && Interlocked.Increment(ref _calls) == 1)
            throw new HttpRequestException("transient test failure");
        return [new TranscriptSegment(0, 1, chunk.FilePath)];
    }
}

sealed class FakeAnalysisProvider : IContentAnalysisProvider
{
    public string ScannerVersion => "contract-test";

    public Task<IReadOnlyList<ScanEvent>> Analyze(
        IReadOnlyList<TranscriptSegment> segments,
        Action<double>? reportProgress,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ScanEvent>>([]);
}

sealed class CapturingTranscriptStore(PrivateTranscript? initial = null) : IPrivateTranscriptStore
{
    public PrivateTranscript? Transcript { get; private set; } = initial;
    public int SaveCount { get; private set; }

    public Task<PrivateTranscript?> Load(
        BookFingerprint fingerprint,
        CancellationToken cancellationToken) => Task.FromResult(Transcript);

    public Task Save(
        BookFingerprint fingerprint,
        PrivateTranscript transcript,
        CancellationToken cancellationToken)
    {
        SaveCount += 1;
        Transcript = transcript;
        return Task.CompletedTask;
    }
}

sealed class FakeProcessRunner(double duration) : IProcessRunner
{
    public Task<ProcessExecutionResult> Run(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (executable == "ffprobe")
        {
            return Task.FromResult(
                new ProcessExecutionResult(
                    0,
                    duration.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    string.Empty));
        }

        File.WriteAllBytes(arguments[^1], [0]);

        return Task.FromResult(
            new ProcessExecutionResult(0, string.Empty, string.Empty));
    }
}
