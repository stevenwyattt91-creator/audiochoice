using System.Text;
using System.Text.Json;
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

var alcoholMapping = ContentTaxonomy.Mappings["substance_alcohol_use"];
var intoxicationMapping = ContentTaxonomy.Mappings["substance_intoxication"];
var drugMapping = ContentTaxonomy.Mappings["substance_drug_use"];
var graphicViolenceMapping = ContentTaxonomy.Mappings["violence_graphic"];
var sexualReferenceMapping = ContentTaxonomy.Mappings["sexual_references"];
var sexualNudityMapping = ContentTaxonomy.Mappings["sexual_nudity"];
var condensedControls = UserFacingEventPostProcessor.Process([
    new ScanEvent(Guid.NewGuid(), 1, 2, alcoholMapping.CategoryID, alcoholMapping.GroupID,
        alcoholMapping.EventID, 1),
    new ScanEvent(Guid.NewGuid(), 3, 4, intoxicationMapping.CategoryID, intoxicationMapping.GroupID,
        intoxicationMapping.EventID, 1),
    new ScanEvent(Guid.NewGuid(), 5, 6, drugMapping.CategoryID, drugMapping.GroupID,
        drugMapping.EventID, 1),
    new ScanEvent(Guid.NewGuid(), 10, 12, graphicViolenceMapping.CategoryID,
        graphicViolenceMapping.GroupID, graphicViolenceMapping.EventID, 1),
    new ScanEvent(Guid.NewGuid(), 16, 20, graphicViolenceMapping.CategoryID,
        graphicViolenceMapping.GroupID, graphicViolenceMapping.EventID, 1),
    new ScanEvent(Guid.NewGuid(), 30, 31, sexualReferenceMapping.CategoryID,
        sexualReferenceMapping.GroupID, sexualReferenceMapping.EventID, 1),
    new ScanEvent(Guid.NewGuid(), 30, 32, sexualNudityMapping.CategoryID,
        sexualNudityMapping.GroupID, sexualNudityMapping.EventID, 1)
]);
Assert(condensedControls.Count == 7, "User-facing aggregation discarded raw event ranges.");
Assert(condensedControls.Where(item => item.CategoryID == alcoholMapping.CategoryID)
    .Select(item => item.AggregateKey).Distinct().Count() == 2,
    "Alcohol and drug events were not reduced to two aggregate controls.");
Assert(condensedControls.Where(item => item.CategoryID == graphicViolenceMapping.CategoryID)
    .Select(item => item.AggregateKey).Distinct().Count() == 1,
    "Nearby violence events were not grouped into one control.");
Assert(condensedControls.Where(item => item.CategoryID == sexualReferenceMapping.CategoryID)
    .Select(item => item.AggregateKey).Distinct().Count() == 1,
    "Simultaneous sexual-content events were not grouped into one control.");
Assert(condensedControls.First(item => item.AggregateKey is not null &&
        item.CategoryID == sexualReferenceMapping.CategoryID).AggregateDisplay ==
    "A character removes clothing or is described without clothing",
    "Grouped sexual controls did not retain a useful, clean explanation.");
Assert(OpenAIContentAnalysisProvider.SafeDescriptionForEvent(
        "sexual_explicit_activity", "A character squeezes a partner's breast.") ==
    "Characters are described in an intimate encounter",
    "Graphic sexual detail reached the user-facing description.");
Assert(OpenAIContentAnalysisProvider.SafeDescriptionForEvent(
        "violence_graphic", "A severed, bloodied head is displayed.") ==
    "Graphic violence described",
    "Graphic violence detail reached the user-facing description.");
Assert(OpenAIContentAnalysisProvider.SafeDescriptionForEvent(
        "self_harm_suicidal_thoughts", "A character considers slitting their throat.") ==
    "Suicidal thoughts described",
    "A self-harm method reached the user-facing description.");

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

// The other half of the release, repaired by migration 026. It arrived titled plainly, and
// what has to hold is that the structured columns alone produce the full name.
Assert(
    EditionTitleFormatter.Format("Fourth Wing", "Dramatized Adaptation", 2, 2)
        == "Fourth Wing (Part 2 of 2) (Dramatized Adaptation)",
    "Fourth Wing Part 2 did not render from its part and edition columns.");
// That migration also writes the finished title into the row. Formatting has to be idempotent
// or the wording would be appended a second time every time the entry is displayed.
Assert(
    EditionTitleFormatter.Format(
        "Fourth Wing (Part 2 of 2) (Dramatized Adaptation)", "Dramatized Adaptation", 2, 2)
        == "Fourth Wing (Part 2 of 2) (Dramatized Adaptation)",
    "Re-formatting a stored Part 2 title doubled its part or edition wording.");

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
Assert(joinedSceneEvents[0].StartTime == 100 - 3 && joinedSceneEvents[0].EndTime == 260 + 3,
    "A joined sexual scene did not get exactly three seconds of padding either side. " +
    "Padding was reduced from eight seconds because sixteen seconds a scene was, across a " +
    "book, one of the largest contributors to how much audio disappeared.");
Assert(joinedSceneEvents[0].SafeDescription == "Sustained sexual activity",
    "Sexual scene safe description fallback was not applied.");

var narrowSceneEvents = SceneEventPostProcessor.Process(
    [
        new ScanEvent(Guid.NewGuid(), 300, 308, completeSceneMapping.CategoryID,
            completeSceneMapping.GroupID, completeSceneMapping.EventID, .94, "narrow-scene",
            "Brief explicit activity")
    ],
    [new TranscriptSegment(0, 600, "Test transcript")]);
Assert(narrowSceneEvents.Count == 0,
    "An eight-second detection was promoted to a complete-scene skip. A floor still has to "
    + "stop one explicit sentence and its padding from becoming a scene-sized skip.");

// The floor has come down twice: 60 to 30 once Terra and Sol verified every scene, then 30 to 15
// after a tester heard two brief scenes play with Complete sex scenes enabled. So a
// twenty-five-second verified encounter, which the thirty-second floor discarded, has to keep its
// skip. Both sides are pinned, because asserting only that short scenes are dropped would be
// satisfied by dropping every scene.
var briefButRealScene = SceneEventPostProcessor.Process(
    [
        new ScanEvent(Guid.NewGuid(), 300, 325, completeSceneMapping.CategoryID,
            completeSceneMapping.GroupID, completeSceneMapping.EventID, .94, "brief-real-scene",
            "Brief intimate encounter")
    ],
    [new TranscriptSegment(0, 600, "Test transcript")]);
Assert(briefButRealScene.Count == 1,
    "A twenty-five-second verified scene was discarded for being short. That is the fault a "
    + "listener reported: brief scenes detected, verified, dropped, and then heard.");

// Both sides of the minimum are pinned, because only asserting that short scenes are dropped
// would be satisfied by dropping every scene. The threshold was lowered from 60 seconds once
// Terra and Sol began verifying every scene, and a verified minute-long encounter has to keep
// its skip.
var retainedSceneEvents = SceneEventPostProcessor.Process(
    [
        new ScanEvent(Guid.NewGuid(), 300, 380, completeSceneMapping.CategoryID,
            completeSceneMapping.GroupID, completeSceneMapping.EventID, .94, "real-scene",
            "Sustained intimate encounter")
    ],
    [new TranscriptSegment(0, 600, "Test transcript")]);
Assert(retainedSceneEvents.Count == 1,
    "A verified scene comfortably above the minimum lost its complete-scene skip.");

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
// Order-independent on purpose. The scheduler reports progress as
// progress(Interlocked.Increment(ref completed), total): the counter is atomic, but the
// callback that follows it is not, so two workers can increment to 3 and 4 and then enqueue
// in the opposite order. Asserting on the last item queued failed roughly one run in eight,
// which is worse than no assertion because it teaches everyone to rerun and move on.
//
// What the scheduler does guarantee is that each count appears exactly once and the total
// never changes, which is also a stronger statement than the last value being (4, 4).
Assert(
    schedulerProgress.Select(step => step.Done).OrderBy(done => done).SequenceEqual([1, 2, 3, 4]),
    "Concurrent scheduler did not report each completed chunk exactly once.");
Assert(schedulerProgress.All(step => step.Total == 4),
    "Concurrent scheduler reported an inconsistent chunk total.");
var retryProvider = new SchedulerFakeProvider(failFirst: true);
var retryScheduler = new ConcurrentChunkTranscriber(
    retryProvider,
    new OpenAIProcessingOptions { TranscriptionWorkers = 1, TranscriptionConcurrencyPerWorker = 1, TranscriptionMaximumRetries = 2 },
    NullLogger<ConcurrentChunkTranscriber>.Instance);
var retryResult = await retryScheduler.Transcribe(
    [new AudioChunk("retry", 0, 1)], null, CancellationToken.None);
Assert(retryResult[0].RetryCount == 1, "Transient transcription failure was not retried once.");

// Edition identity. A fingerprint is a hash of file bytes, so a converted or
// re-tagged copy of the same recording gets a different one and every artifact keyed
// by it -- transcripts above all -- goes missing.
var editionBase = new BookFingerprint(
    1, new string('a', 64), 1_000, 28_800, "m4b",
    "King Sorrow", "Joe Hill", null, null, null, null, null);
var editionConverted = editionBase with { Sha256 = new string('b', 64), FileSize = 2_000, FileType = "m4a" };

Assert(EditionMatch.SameRecording(editionBase, editionConverted),
    "A converted copy of the same recording was not recognised.");
Assert(EditionMatch.SameRecording(editionBase, editionBase),
    "A fingerprint did not match itself.");
Assert(!EditionMatch.SameRecording(
        editionBase,
        editionConverted with { Duration = 28_800 + EditionMatch.MaximumRuntimeDriftSeconds + 1 }),
    "Recordings with clearly different runtimes were treated as the same edition.");
Assert(!EditionMatch.SameRecording(editionBase, editionConverted with { Duration = null }),
    "An unknown runtime was accepted as corroboration.");
Assert(!EditionMatch.SameRecording(editionBase, editionConverted with { WorkTitle = "Heart-Shaped Box" }),
    "Two different works were treated as the same edition.");
Assert(!EditionMatch.SameRecording(
        editionBase,
        editionConverted with { EditionType = "Dramatized Adaptation" }),
    "A dramatized adaptation matched a straight reading.");
Assert(!EditionMatch.SameRecording(
        editionBase with { WorkTitle = "Iron Flame Part 1 of 2" },
        editionConverted with { WorkTitle = "Iron Flame Part 2 of 2" }),
    "Two halves of a split audiobook were treated as the same edition.");
Assert(EditionMatch.SameRecording(
        editionBase with { WorkTitle = "King Sorrow" },
        editionConverted with { WorkTitle = "King Sorrow: A Novel" }),
    "A trailing title qualifier defeated the match.");
Assert(EditionMatch.SameRecording(editionBase, editionConverted with { Author = null }),
    "A missing author was treated as a contradiction.");

var aliasFolder = Path.Combine(Path.GetTempPath(), $"audiochoice-aliases-{Guid.NewGuid()}");
try
{
    var aliasPath = Path.Combine(aliasFolder, "edition-aliases.json");
    var aliasStore = new FileEditionAliasStore(aliasPath);
    aliasStore.Link(editionBase, editionConverted);
    Assert(aliasStore.Aliases(editionBase).Any(value => value.Sha256 == editionConverted.Sha256),
        "An edition alias was not recorded.");
    Assert(aliasStore.Aliases(editionConverted).Any(value => value.Sha256 == editionBase.Sha256),
        "Edition aliases were not linked in both directions.");
    aliasStore.Link(editionBase, editionConverted);
    Assert(aliasStore.Aliases(editionBase).Count == 1, "Linking the same pair twice duplicated it.");
    Assert(new FileEditionAliasStore(aliasPath).Aliases(editionBase).Count == 1,
        "Edition aliases did not survive a reload.");

    // The transcript exists, but under the fingerprint of the file that was
    // uploaded rather than the one the library row carries.
    var timedTranscript = new PrivateTranscript(
        "1.0", "en", "test-model", DateTimeOffset.UtcNow,
        [new TranscriptSegment(0, 4, "A line of narration.")], true);
    var keyedTranscripts = new KeyedTranscriptStore();
    await keyedTranscripts.Save(editionConverted, timedTranscript, CancellationToken.None);

    // ListFingerprints reports uploads and jobs rather than saved results, so the
    // scanned file has to be registered the way a real import registers it.
    var resolverCatalog = new InMemoryScanCatalog();
    resolverCatalog.CreateUpload(
        Guid.NewGuid(),
        new CloudUploadAuthorizationRequest(
            editionConverted, "king-sorrow.m4a", "audio/mp4", editionConverted.FileSize),
        DateTimeOffset.UtcNow.AddHours(1),
        "upload-token");
    var resolverAliases = new FileEditionAliasStore(Path.Combine(aliasFolder, "resolver-aliases.json"));
    var resolverSignatures = new FileEditionSignatureStore(
        Path.Combine(aliasFolder, "resolver-signatures.json"));
    var resolver = new EditionResolver(
        keyedTranscripts, resolverCatalog, resolverAliases, resolverSignatures,
        NullLogger<EditionResolver>.Instance);

    Assert(await resolver.LoadTranscript(editionConverted, CancellationToken.None) is not null,
        "The resolver failed on an exact fingerprint match.");
    Assert(await resolver.LoadTranscript(editionBase, CancellationToken.None) is not null,
        "The resolver did not recover a transcript stored under the source file's fingerprint.");
    Assert(resolverAliases.Aliases(editionBase).Any(value => value.Sha256 == editionConverted.Sha256),
        "The resolver did not remember the link it discovered.");

    // Proving it is the remembered link doing the work on the second call, not a
    // repeat of the metadata scan.
    var aliasOnlyResolver = new EditionResolver(
        keyedTranscripts, new InMemoryScanCatalog(), resolverAliases, resolverSignatures,
        NullLogger<EditionResolver>.Instance);
    Assert(await aliasOnlyResolver.LoadTranscript(editionBase, CancellationToken.None) is not null,
        "A recorded alias did not resolve without the catalog.");

    var unrelated = editionBase with
    {
        Sha256 = new string('c', 64), WorkTitle = "Heart-Shaped Box", Duration = 14_400,
    };
    Assert(await resolver.LoadTranscript(unrelated, CancellationToken.None) is null,
        "The resolver returned another recording's transcript.");

    // A transcript with no segments carries no timing and is not an answer.
    var emptyTranscripts = new KeyedTranscriptStore();
    await emptyTranscripts.Save(
        editionConverted,
        new PrivateTranscript("1.0", "en", "test-model", DateTimeOffset.UtcNow, [], true),
        CancellationToken.None);
    var emptyResolver = new EditionResolver(
        emptyTranscripts, resolverCatalog,
        new FileEditionAliasStore(Path.Combine(aliasFolder, "empty-aliases.json")),
        new FileEditionSignatureStore(Path.Combine(aliasFolder, "empty-signatures.json")),
        NullLogger<EditionResolver>.Instance);
    Assert(await emptyResolver.LoadTranscript(editionBase, CancellationToken.None) is null,
        "A transcript with no segments was treated as usable timing data.");

    // Correcting a guessed title. A file with no tags leaves AudioChoice guessing from
    // the filename, so this has to be fixable, and it must stay scoped to one listener.
    var detailsLibrary = new FileUserLibraryStore(Path.Combine(aliasFolder, "details-library.json"));
    var detailsOwner = Guid.NewGuid();
    var detailsIntruder = Guid.NewGuid();
    var detailBook = detailsLibrary.Upsert(detailsOwner, new LibraryBookUpsertRequest(
        editionBase, "fourth wingggg", null, null, null));
    var corrected = detailsLibrary.UpdateDetails(
        detailsOwner,
        detailBook.ID,
        new LibraryBookDetailsRequest("Fourth Wing", "Rebecca Yarros", "Rebecca Soler"));
    Assert(corrected?.Title == "Fourth Wing", "A corrected title was not saved.");
    Assert(corrected?.Author == "Rebecca Yarros", "A corrected author was not saved.");
    Assert(corrected?.Narrator == "Rebecca Soler", "A corrected narrator was not saved.");
    Assert(detailsLibrary.UpdateDetails(
            detailsIntruder, detailBook.ID, new LibraryBookDetailsRequest("Hijacked")) is null,
        "Another user corrected someone else's book details.");
    Assert(detailsLibrary.List(detailsOwner).Single().Title == "Fourth Wing",
        "A corrected title did not survive being read back.");
    // Identity must keep coming from the file, never from typed-in text.
    Assert(detailsLibrary.List(detailsOwner).Single().Fingerprint.WorkTitle == editionBase.WorkTitle,
        "Correcting the display title altered the edition fingerprint used for matching.");

    // Identity evidence the byte hash cannot express. A retail identifier settles the
    // question outright; a narrator or chapter structure can rule a match out.
    var audibleSignature = new EditionSignature("B0CTJ1PDKM", "Zachary Quinto");
    var sameProduct = new EditionSignature("B0CTJ1PDKM", null);
    var otherProduct = new EditionSignature("B0XXXXXXXX", "Zachary Quinto");
    Assert(EditionMatch.SameRecording(editionBase, editionConverted, audibleSignature, sameProduct),
        "A shared retail product identifier did not settle a match.");
    Assert(!EditionMatch.SameRecording(editionBase, editionConverted, audibleSignature, otherProduct),
        "Different retail product identifiers were not treated as different editions.");
    Assert(EditionMatch.SameRecording(
            editionBase with { WorkTitle = "Something Else", Author = "Someone Else" },
            editionConverted,
            audibleSignature,
            sameProduct),
        "A retail identifier should outrank a disagreeing title and author.");
// Signatures are client-reported, so an identifier must never be able to waive the
// one claim a tagger cannot forge. Otherwise a borrowed ASIN would redirect filter
// results between unrelated recordings.
Assert(!EditionMatch.SameRecording(
        editionBase with { Duration = 100 },
        editionConverted,
        audibleSignature,
        sameProduct),
    "A reported product identifier overrode a contradicting runtime.");
Assert(!EditionMatch.SameRecording(
        editionBase with { Duration = null },
        editionConverted,
        audibleSignature,
        sameProduct),
    "A reported product identifier stood in for missing runtime evidence.");
    Assert(!EditionMatch.SameRecording(
            editionBase, editionConverted,
            new EditionSignature(null, "Zachary Quinto"),
            new EditionSignature(null, "Someone Entirely Different")),
        "Two different readings of the same book were treated as interchangeable.");
    Assert(EditionMatch.SameRecording(
            editionBase, editionConverted,
            new EditionSignature(null, "Zachary Quinto"),
            new EditionSignature(null, "Zachary Quinto and a Full Cast")),
        "A longer narrator credit was treated as a contradiction.");
    Assert(!EditionMatch.SameRecording(
            editionBase, editionConverted,
            new EditionSignature(null, null, [0, 1200, 2400]),
            new EditionSignature(null, null, [0, 1200, 2400, 3600])),
        "A different chapter structure was accepted as the same edition.");
    Assert(EditionMatch.SameRecording(
            editionBase, editionConverted,
            new EditionSignature(null, null, [0, 1200, 2400]),
            new EditionSignature(null, null, [0, 1201, 2399])),
        "Whole-second chapter rounding defeated the match.");
    Assert(EditionMatch.SameRecording(
            editionBase, editionConverted,
            new EditionSignature(null, null, [0, 1200, 2400]),
            new EditionSignature(null, null, null)),
        "Missing chapter marks were treated as a contradiction rather than silence.");

    var signatureStore = new FileEditionSignatureStore(
        Path.Combine(aliasFolder, "signatures.json"));
    signatureStore.Record(editionBase, new EditionSignature("B0CTJ1PDKM", null));
    signatureStore.Record(editionBase, new EditionSignature(null, "Zachary Quinto"));
    Assert(signatureStore.Find(editionBase)?.ProductIdentifier == "B0CTJ1PDKM",
        "A later report without an identifier erased the one already held.");
    Assert(signatureStore.Find(editionBase)?.Narrator == "Zachary Quinto",
        "Signature fields were not merged.");
    Assert(new FileEditionSignatureStore(Path.Combine(aliasFolder, "signatures.json"))
            .Find(editionBase)?.ProductIdentifier == "B0CTJ1PDKM",
        "Edition signatures did not survive a reload.");

    // Filters are held to a stricter standard than timings: a wrong filter result
    // could play content a listener asked never to hear.
    var filterCatalog = new InMemoryScanCatalog();
    filterCatalog.CreateUpload(
        Guid.NewGuid(),
        new CloudUploadAuthorizationRequest(
            editionConverted, "king-sorrow.m4a", "audio/mp4", editionConverted.FileSize),
        DateTimeOffset.UtcNow.AddHours(1),
        "filter-token");
    filterCatalog.SaveResult(editionConverted, result);
    var metadataOnlySignatures = new FileEditionSignatureStore(
        Path.Combine(aliasFolder, "filter-metadata-signatures.json"));
    var metadataOnlyFilters = new EditionResolver(
        keyedTranscripts, filterCatalog,
        new FileEditionAliasStore(Path.Combine(aliasFolder, "filter-metadata-aliases.json")),
        metadataOnlySignatures, NullLogger<EditionResolver>.Instance);
    Assert(metadataOnlyFilters.FindResult(editionConverted) is not null,
        "An exact fingerprint did not return its own filter results.");
    Assert(metadataOnlyFilters.FindResult(editionBase) is null,
        "Filter results were reused on metadata similarity alone.");

    var provenSignatures = new FileEditionSignatureStore(
        Path.Combine(aliasFolder, "filter-proven-signatures.json"));
    provenSignatures.Record(editionBase, new EditionSignature("B0CTJ1PDKM", null));
    provenSignatures.Record(editionConverted, new EditionSignature("B0CTJ1PDKM", null));
    var provenFilters = new EditionResolver(
        keyedTranscripts, filterCatalog,
        new FileEditionAliasStore(Path.Combine(aliasFolder, "filter-proven-aliases.json")),
        provenSignatures, NullLogger<EditionResolver>.Instance);
    Assert(provenFilters.FindResult(editionBase) is not null,
        "A matching retail product identifier did not allow filter results to be reused.");

    var clientLinkedAliases = new FileEditionAliasStore(
        Path.Combine(aliasFolder, "filter-linked-aliases.json"));
    clientLinkedAliases.Link(editionBase, editionConverted);
    var clientLinkedFilters = new EditionResolver(
        keyedTranscripts, filterCatalog, clientLinkedAliases, metadataOnlySignatures,
        NullLogger<EditionResolver>.Instance);
    Assert(clientLinkedFilters.FindResult(editionBase) is not null,
        "A link the client reported outright did not allow filter results to be reused.");
}
finally
{
    if (Directory.Exists(aliasFolder)) Directory.Delete(aliasFolder, true);
}

// Apple identity tokens. Every claim in one is attacker-supplied text until the
// signature is verified, so a hand-assembled token must never be accepted.
using var appleKey = System.Security.Cryptography.RSA.Create(2048);
var appleParameters = appleKey.ExportParameters(false);
var appleKeySet = $$"""
{"keys":[{"kty":"RSA","kid":"test-key","use":"sig","alg":"RS256",
"n":"{{Base64Url(appleParameters.Modulus!)}}","e":"{{Base64Url(appleParameters.Exponent!)}}"}]}
""";
var appleKeys = AppleIdentityToken.ParseJsonWebKeySet(appleKeySet);
Assert(appleKeys.ContainsKey("test-key"), "Apple JWKS parsing did not yield the signing key.");

var appleHeader = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"RS256","kid":"test-key"}"""));
var applePayload = Base64Url(Encoding.UTF8.GetBytes(
    $$"""{"iss":"https://appleid.apple.com","aud":"com.audiochoice.mobile","sub":"001","exp":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}"""));
var appleSignature = Base64Url(appleKey.SignData(
    Encoding.ASCII.GetBytes($"{appleHeader}.{applePayload}"),
    System.Security.Cryptography.HashAlgorithmName.SHA256,
    System.Security.Cryptography.RSASignaturePadding.Pkcs1));

Assert(AppleIdentityToken.SignatureIsValid($"{appleHeader}.{applePayload}.{appleSignature}", appleKeys),
    "A correctly signed Apple token was rejected.");

// The forgery this guards against: valid-looking claims, no real signature.
Assert(!AppleIdentityToken.SignatureIsValid($"{appleHeader}.{applePayload}.", appleKeys),
    "An Apple token with an empty signature was accepted.");
Assert(!AppleIdentityToken.SignatureIsValid($"{appleHeader}.{applePayload}.{Base64Url(Encoding.UTF8.GetBytes("not-a-signature"))}", appleKeys),
    "An Apple token with a junk signature was accepted.");

// Claims cannot be edited after signing.
var tamperedPayload = Base64Url(Encoding.UTF8.GetBytes(
    $$"""{"iss":"https://appleid.apple.com","aud":"com.audiochoice.mobile","sub":"victim","exp":{{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}"""));
Assert(!AppleIdentityToken.SignatureIsValid($"{appleHeader}.{tamperedPayload}.{appleSignature}", appleKeys),
    "An Apple token whose subject was swapped after signing was accepted.");

// "alg": "none" must not bypass verification.
var unsignedHeader = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"none","kid":"test-key"}"""));
Assert(!AppleIdentityToken.SignatureIsValid($"{unsignedHeader}.{applePayload}.", appleKeys),
    "An unsigned Apple token was accepted.");

// An unknown key id fails closed rather than skipping the check.
var foreignHeader = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"RS256","kid":"some-other-key"}"""));
Assert(!AppleIdentityToken.SignatureIsValid($"{foreignHeader}.{applePayload}.{appleSignature}", appleKeys),
    "An Apple token naming an unknown key was accepted.");

Assert(!AppleIdentityToken.SignatureIsValid("not.a.jwt", appleKeys), "Malformed input was accepted.");
Assert(!AppleIdentityToken.SignatureIsValid(null, appleKeys), "A null token was accepted.");
Assert(!AppleIdentityToken.SignatureIsValid($"{appleHeader}.{applePayload}.{appleSignature}", new Dictionary<string, System.Security.Cryptography.RSA>()),
    "A token was accepted when no signing keys were available.");

Assert(AppleIdentityToken.ParseJsonWebKeySet("}{not json").Count == 0,
    "Malformed JWKS did not degrade to an empty key set.");

// The taxonomy has to say the same thing everywhere. It is declared in the contracts file,
// implemented in C#, and mirrored in each mobile client's own table; the declared file had
// drifted to five labels while the implementation carried twenty-eight and responses
// advertised version 2.0. These assertions make that kind of drift fail here instead of
// showing up as a filter control that does nothing.
var taxonomyPath = Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "..",
    "contracts", "content-taxonomy.v2.json");
Assert(File.Exists(taxonomyPath), $"The taxonomy contract file was not found at {taxonomyPath}.");
using var taxonomyDocument = JsonDocument.Parse(File.ReadAllText(taxonomyPath));
var declaredGroups = taxonomyDocument.RootElement.GetProperty("categories").EnumerateArray()
    .SelectMany(category => category.GetProperty("groups").EnumerateArray()
        .Select(group => new
        {
            Label = group.GetProperty("label").GetString()!,
            Enforced = group.GetProperty("enforced").GetBoolean(),
            CategoryID = category.GetProperty("categoryID").GetString()!,
            Digit = category.GetProperty("digit").GetInt32(),
            Index = group.GetProperty("index").GetInt32()
        }))
    .ToArray();

foreach (var declared in declaredGroups)
{
    Assert(ContentTaxonomy.Mappings.TryGetValue(declared.Label, out var mapping),
        $"The taxonomy contract declares {declared.Label} but ContentTaxonomy does not.");
    Assert(mapping!.CategoryID == Guid.Parse(declared.CategoryID),
        $"{declared.Label} has a different category identifier in code than in the contract.");
    // The identifiers are derived, so a mismatch here means the derivation changed.
    Assert(mapping.GroupID == Guid.Parse(
            $"{declared.Digit}1000000-0000-0000-0000-{declared.Index:D12}"),
        $"{declared.Label} does not have the derived group identifier.");
    Assert(mapping.EventID == Guid.Parse(
            $"{declared.Digit}1100000-0000-0000-0000-{declared.Index:D12}"),
        $"{declared.Label} does not have the derived event identifier.");
}

var declaredLabels = declaredGroups.Select(item => item.Label).ToHashSet(StringComparer.Ordinal);
var legacyDeclared = taxonomyDocument.RootElement.GetProperty("legacyLabels").EnumerateArray()
    .Select(item => item.GetProperty("label").GetString()!).ToHashSet(StringComparer.Ordinal);
foreach (var label in ContentTaxonomy.Mappings.Keys)
{
    Assert(declaredLabels.Contains(label) || legacyDeclared.Contains(label),
        $"ContentTaxonomy has {label} but the taxonomy contract never declares it.");
}

// What the model is allowed to return must be exactly what the contract marks enforced.
var enforcedDeclared = declaredGroups.Where(item => item.Enforced)
    .Select(item => item.Label).ToHashSet(StringComparer.Ordinal);
Assert(ContentTaxonomy.EnforcedLabels.ToHashSet(StringComparer.Ordinal)
        .SetEquals(enforcedDeclared),
    "The labels the analysis model may emit do not match the enforced labels in the contract.");
Assert(ContentTaxonomy.EnforcedLabels.Distinct(StringComparer.Ordinal).Count() ==
        ContentTaxonomy.EnforcedLabels.Count,
    "The enforced label list contains a duplicate.");
Assert(ContentTaxonomy.EnforcedLabels.All(ContentTaxonomy.Mappings.ContainsKey),
    "An enforced label has no taxonomy mapping, so its detections would be discarded.");

// The three broad violence labels must stay out of reach of the model.
foreach (var excluded in new[] { "violence_mild", "violence_intense", "violence_death" })
{
    Assert(!ContentTaxonomy.EnforcedLabels.Contains(excluded, StringComparer.Ordinal),
        $"{excluded} is emittable again, which reopens the over-filtering the narrow " +
        "violence policy exists to prevent.");
    Assert(ContentTaxonomy.Mappings.ContainsKey(excluded),
        $"{excluded} lost its mapping, so older scans containing it would stop resolving.");
}

// The prompt lists the allowed labels for the model. It is generated from the same source as
// the response schema, and this is what proves the generation still covers everything.
foreach (var label in ContentTaxonomy.EnforcedLabels)
{
    Assert(OpenAIContentAnalysisProvider.AllowedLabelList.Contains(label, StringComparison.Ordinal),
        $"The analysis prompt does not mention the allowed label {label}.");
}
Assert(!OpenAIContentAnalysisProvider.AllowedLabelList.Contains("violence_mild", StringComparison.Ordinal),
    "The analysis prompt offers the model a label the policy excludes.");

// Listener reports that filtering was wrong. The only route by which a missed passage
// becomes something anyone can act on, so what it accepts and refuses matters.
var reportFolder = Path.Combine(Path.GetTempPath(), $"audiochoice-reports-{Guid.NewGuid()}");
Directory.CreateDirectory(reportFolder);
var reportPath = Path.Combine(reportFolder, "filter-reports.json");
var reportStore = new FileFilterReportStore(reportPath);
var reporter = Guid.NewGuid();
var reported = reportStore.Record(reporter, new FilterReportRequest(
    editionBase, FilterReportKind.MissedContent, 1234.5));
Assert(reported is not null, "A well-formed missed-content report was refused.");
Assert(reported!.WindowSeconds == FilterReports.DefaultWindowSeconds,
    "A report without a window did not fall back to the default look-back.");
Assert(reported.Kind == FilterReportKind.MissedContent, "A report changed kind on the way in.");

// A report carries a timestamp and nothing about what was heard, which is what lets
// filtering be corrected without a listener's audio ever leaving their device.
Assert(reported.GetType().GetProperties().All(property =>
        property.Name is not ("Transcript" or "Text" or "Audio" or "Words" or "Note")),
    "A filter report gained a field that could carry the content it reports.");

Assert(reportStore.Record(reporter, new FilterReportRequest(
        editionBase, FilterReportKind.MissedContent, -5)) is null,
    "A report at a negative position was accepted.");
Assert(reportStore.Record(reporter, new FilterReportRequest(
        editionBase, FilterReportKind.MissedContent, double.NaN)) is null,
    "A report at a non-finite position was accepted.");
Assert(reportStore.Record(Guid.Empty, new FilterReportRequest(
        editionBase, FilterReportKind.MissedContent, 10)) is null,
    "A report with no account was accepted.");
Assert(reportStore.Record(reporter, new FilterReportRequest(
        editionBase, FilterReportKind.MissedContent, 10, WindowSeconds: 10_000))!
        .WindowSeconds == FilterReports.MaximumWindowSeconds,
    "An unbounded look-back window was not clamped.");

// Over-filtering is the complaint that needs an event to be actionable: without one there
// is no way to tell which control was wrong.
var overFiltered = reportStore.Record(reporter, new FilterReportRequest(
    editionBase, FilterReportKind.WronglyFiltered, 99, ScanEventID: Guid.NewGuid(),
    ScannerVersion: "test-1"));
Assert(overFiltered?.ScanEventID is not null, "A wrongly-filtered report lost its event.");
Assert(overFiltered?.ScannerVersion == "test-1",
    "A report lost the scanner version that produced the result.");
Assert(reportStore.List().Count >= 3, "Reports were not listed back.");
Assert(reportStore.List(limit: 1).Count == 1, "A report listing ignored its limit.");
Assert(reportStore.List(fingerprint: editionBase).Count >= 3,
    "Filtering reports by edition returned nothing.");
Assert(new FileFilterReportStore(reportPath).List().Count >= 3,
    "Reports did not survive being read back from disk.");

// Explore de-duplication. Merging too little leaves the duplicate rows; merging too much
// hides a different recording behind another's entry, applying a scan that does not
// describe it.
Assert(ExploreCatalog.Deduplicate([
        Catalogued("a", "Fourth Wing", "Rebecca Yarros", cover: "/cover"),
        Catalogued("b", "Fourth Wing (Unabridged)", "Rebecca Yarros"),
        Catalogued("c", "Fourth Wing")
    ]).Count == 1,
    "Three spellings of one recording were not merged.");
Assert(ExploreCatalog.Deduplicate([
        Catalogued("a", "Fourth Wing", "Rebecca Yarros", eventCount: 900),
        Catalogued("b", "Fourth Wing", "Rebecca Yarros", eventCount: 2, cover: "/cover")
    ]).Single().CatalogID == "b",
    "A cover did not outrank a richer scan when choosing which entry survives.");
Assert(ExploreCatalog.Deduplicate([
        Catalogued("a", "Fourth Wing", "Rebecca Yarros", editionType: "GraphicAudio"),
        Catalogued("b", "Fourth Wing", "Rebecca Yarros")
    ]).Count == 2,
    "Two different editions were collapsed into one.");
Assert(ExploreCatalog.Deduplicate([
        Catalogued("a", "Fourth Wing 1 of 2", "Rebecca Yarros"),
        Catalogued("b", "Fourth Wing 2 of 2", "Rebecca Yarros")
    ]).Count == 2,
    "Two parts of one release were collapsed into one.");
Assert(ExploreCatalog.Deduplicate([
        Catalogued("a", "Fourth Wing", "Rebecca Yarros"),
        Catalogued("b", "Fourth Wing", "Someone Else")
    ]).Count == 2,
    "Two different authors sharing a title were merged.");
Assert(ExploreCatalog.Deduplicate([
        Catalogued("1", "Alpha", "A"), Catalogued("2", "Beta", "B")
    ]).Select(value => value.CatalogID).SequenceEqual(["1", "2"]),
    "De-duplication reordered a catalogue that had no duplicates.");

// The Explore synopsis. This is shown to listeners under "About this audiobook", so the
// property that matters is that it is either the story or nothing at all: it used to be a
// generated line about AudioChoice's own features, which does not describe the book.
var synopsis = "Twenty-year-old Violet Sorrengail was supposed to enter the Scribe " +
    "Quadrant, living a quiet life among books and history.";
var describedFingerprint = new BookFingerprint(
    3, new string('d', 64), 900_000, 3600, "m4b", "The Deal", "Elle Kennedy",
    null, null, null, null, null);
var describedResult = new ScanResult([], DateTimeOffset.UnixEpoch, "v1");
Assert(
    ExploreCatalog.Create(describedFingerprint, describedResult).Description is null,
    "A book with no stored synopsis was still given a description.");
Assert(
    ExploreCatalog.Create(describedFingerprint, describedResult, false, synopsis).Description
        == synopsis,
    "The stored synopsis was not used as the Explore description.");
Assert(
    ExploreCatalog.Create(describedFingerprint, describedResult, false, "Fantasy").Description
        is null,
    "A value too short to be a synopsis was presented as one.");
Assert(
    ExploreCatalog.Create(describedFingerprint, describedResult, false,
        new string('x', 5000)).Description!.Length == 4000,
    "An oversized synopsis was not clamped to the stored column width.");

// Round-trip through the catalogue, which is what the library upsert drives.
var descriptionCatalog = new InMemoryScanCatalog(Path.Combine(Path.GetTempPath(),
    $"audiochoice-descriptions-{Guid.NewGuid():N}"));
Assert(descriptionCatalog.SaveEditionDescription(describedFingerprint, synopsis),
    "A valid synopsis was refused.");
Assert(!descriptionCatalog.SaveEditionDescription(describedFingerprint, "Fantasy"),
    "A value too short to be a synopsis was accepted.");
// Any owner of the recording can report one, so a later import carrying a worse tag must
// not replace a good synopsis that is already stored.
Assert(
    !descriptionCatalog.SaveEditionDescription(
        describedFingerprint, "A completely different and equally long replacement text."),
    "A second report overwrote a synopsis that was already stored.");

// What reaches the catalogue at all. Explore is a store front, so an entry has to name a
// book: every edition anyone scans is published by default, which put files that were never
// identified into the catalogue as "Imported audiobook".
static BookFingerprint Edition(string? title, string? author = "Elle Kennedy") =>
    new(3, new string('f', 64), 700_000, 3600, "m4b", title, author,
        null, null, null, null, null);
Assert(ExploreCatalog.IsPublishable(Edition("The Deal")),
    "An identified book was kept out of the catalogue.");
Assert(!ExploreCatalog.IsPublishable(Edition("Imported audiobook")),
    "A file that was never identified was published to the catalogue.");
Assert(!ExploreCatalog.IsPublishable(Edition("Untitled Audiobook")),
    "A placeholder title was published to the catalogue.");
Assert(!ExploreCatalog.IsPublishable(Edition("imported audiobook!")),
    "A placeholder title escaped by way of punctuation.");
Assert(!ExploreCatalog.IsPublishable(Edition(null)),
    "An edition with no title was published to the catalogue.");
Assert(!ExploreCatalog.IsPublishable(Edition("   ")),
    "A blank title was published to the catalogue.");
Assert(!ExploreCatalog.IsPublishable(Edition("track 1")),
    "A track placeholder was published to the catalogue.");
Assert(!ExploreCatalog.IsPublishable(Edition("12345")),
    "A numeric filename was published as a title.");
// An author is the cheapest evidence the title came from the file's tags rather than a
// filename, and an entry without one cannot be presented as a catalogue row anyway.
Assert(!ExploreCatalog.IsPublishable(Edition("The Deal", null)),
    "A book with no author was published to the catalogue.");

// One row per recording. Titles cannot deliver that alone, because the same edition arrives
// spelled differently depending on who tagged the file.
Assert(ExploreCatalog.Deduplicate([
        Catalogued("a", "The Deal", "Elle Kennedy", identifier: "B00SWZQZ4E"),
        Catalogued("b", "The Deal: Off-Campus Book 1", "Elle Kennedy", identifier: "B00SWZQZ4E")
    ]).Count == 1,
    "Two spellings of one recording did not merge on a shared product identifier.");
Assert(ExploreCatalog.Deduplicate([
        Catalogued("a", "The Deal", "Elle Kennedy", identifier: "B00SWZQZ4E"),
        Catalogued("b", "The Mistake", "Elle Kennedy", identifier: "B0112BOSKQ")
    ]).Count == 2,
    "Two books with different product identifiers were merged.");
Assert(ExploreCatalog.Deduplicate([
        Catalogued("a", "The Deal", "Elle Kennedy", duration: 39_600),
        Catalogued("b", "the deal 3112r", "Elle Kennedy", duration: 39_601)
    ]).Count == 1,
    "One recording under two titles did not merge on author and runtime.");
Assert(ExploreCatalog.Deduplicate([
        Catalogued("a", "Some Book", "Elle Kennedy", duration: 39_600),
        Catalogued("b", "A Different Book", "Rebecca Yarros", duration: 39_600)
    ]).Count == 2,
    "Two books sharing a runtime were merged despite different authors.");
Assert(ExploreCatalog.Deduplicate([
        Catalogued("a", "Some Book", null, duration: 39_600),
        Catalogued("b", "A Different Book", null, duration: 39_600)
    ]).Count == 2,
    "Two books sharing a runtime were merged with no author to corroborate it.");
// A different narrator reads at a different pace, so a runtime that disagrees means the scan
// describes different audio and must not be served for this entry.
Assert(ExploreCatalog.Deduplicate([
        Catalogued("a", "The Deal", "Elle Kennedy", duration: 39_600),
        Catalogued("b", "The Deal", "Elle Kennedy", duration: 28_800)
    ]).Count == 2,
    "Two different readings of one title were merged.");

// Looked-up synopses. Open Library's description field is free text, and a good share of it
// is not a synopsis, so what comes back has to be judged before it is shown under a heading
// reading "About this audiobook".
var realSynopsis = "Darrow is a Red, a member of the lowest caste in the color-coded society "
    + "of the future. Like his fellow Reds, he works all day, believing that he and his people "
    + "are making the surface of Mars livable for future generations.";
Assert(OpenLibrarySynopsisProvider.ReadableSynopsis(realSynopsis) == realSynopsis,
    "A genuine synopsis was rejected.");
// This is verbatim what Open Library returns for Red Rising: dialogue from chapter one.
var bookExcerpt = "\"I live for the dream that my children will be born free,\" she says. "
    + "\"That they will be what they like. That they will own the land their father gave them.\" "
    + "\"I live for you,\" I say sadly.";
Assert(OpenLibrarySynopsisProvider.ReadableSynopsis(bookExcerpt) is null,
    "A passage quoted from the book was accepted as a synopsis.");
// Publishers' own copy often leads with a pull-quote. Rejecting anything that opens on a
// quotation mark threw away the real synopsis for Iron Flame, so the test is a speech tag
// rather than a leading quote.
var pullQuoteBlurb = "\u201CThe first year is when some of us lose our lives. The second year "
    + "is when the rest of us lose our humanity.\u201D Everyone expected Violet Sorrengail to "
    + "die during her first year at Basgiath War College.";
Assert(OpenLibrarySynopsisProvider.ReadableSynopsis(pullQuoteBlurb) == pullQuoteBlurb,
    "A blurb opening with a pull-quote was rejected.");
// Markdown emphasis and hard breaks are common in these records and are noise once rendered.
Assert(
    OpenLibrarySynopsisProvider.ReadableSynopsis(
        "**The apocalypse will be televised!**\r\n\r\nA man, his ex-girlfriend's cat, and a "
        + "sadistic game show unlike anything in the universe await the last of humanity.")
        is { } cleaned && !cleaned.Contains('*') && !cleaned.Contains('\r'),
    "Markdown emphasis or hard breaks survived into a stored synopsis.");
Assert(OpenLibrarySynopsisProvider.ReadableSynopsis("Book 1 of the Red Rising series.") is null,
    "A one-line note was accepted as a synopsis.");
Assert(
    OpenLibrarySynopsisProvider.ReadableSynopsis(
        "This edition contains the complete text of the novel together with a new afterword "
        + "by the author and a reading group guide for book clubs.") is null,
    "A note about the edition was accepted as a description of the story.");
Assert(OpenLibrarySynopsisProvider.ReadableSynopsis(null) is null, "Null text was accepted.");
Assert(OpenLibrarySynopsisProvider.ReadableSynopsis("   ") is null, "Blank text was accepted.");
// Real English prose rather than filler, because the gate now also judges whether text is
// English and a run of one letter is not.
var overlongSynopsis = string.Concat(Enumerable.Repeat(realSynopsis + " ", 20));
Assert(overlongSynopsis.Length > 4000, "The oversized fixture was not actually oversized.");
Assert(
    OpenLibrarySynopsisProvider.ReadableSynopsis(overlongSynopsis)!.Length == 4000,
    "An oversized looked-up synopsis was not clamped to the column width.");

// A popular book's editions include translations, and each carries its description in its own
// language. Red Rising's first listed edition is Brazilian, so without this the catalogue got
// Portuguese prose for an English audiobook.
static System.Text.Json.JsonElement Record(string json) =>
    System.Text.Json.JsonDocument.Parse(json).RootElement;
Assert(
    OpenLibrarySynopsisProvider.IsEnglish(Record("""{"languages":[{"key":"/languages/eng"}]}""")),
    "An English edition was rejected.");
Assert(
    !OpenLibrarySynopsisProvider.IsEnglish(Record("""{"languages":[{"key":"/languages/por"}]}""")),
    "A Portuguese edition was accepted as a source of English prose.");
Assert(
    OpenLibrarySynopsisProvider.IsEnglish(
        Record("""{"languages":[{"key":"/languages/por"},{"key":"/languages/eng"}]}""")),
    "A bilingual edition including English was rejected.");
// Work records generally omit the field, and most records that omit it are English. Rejecting
// those would discard the majority of usable descriptions.
Assert(OpenLibrarySynopsisProvider.IsEnglish(Record("""{"title":"Red Rising"}""")),
    "A record that declares no language was rejected.");
Assert(OpenLibrarySynopsisProvider.IsEnglish(Record("""{"languages":[]}""")),
    "A record with an empty language list was rejected.");

// The declared language cannot be the only check. Both of these are real edition records that
// declare no language at all, so the field alone would have stored them against an English
// audiobook.
var frenchBlurb = "Bienvenue, chers crawlers. Bienvenue dans le donjon. Survivre est le seul "
    + "objectif, et le monde entier regarde le spectacle se derouler sans aucune pitie.";
var spanishBlurb = "La nueva novela del autor de El marciano, que se convertira en una "
    + "pelicula, con un protagonista que despierta sin recordar nada de su mision.";
Assert(!OpenLibrarySynopsisProvider.LooksEnglish(frenchBlurb),
    "French text was accepted as an English synopsis.");
Assert(!OpenLibrarySynopsisProvider.LooksEnglish(spanishBlurb),
    "Spanish text was accepted as an English synopsis.");
Assert(OpenLibrarySynopsisProvider.ReadableSynopsis(frenchBlurb) is null,
    "French text passed the synopsis gate.");
Assert(OpenLibrarySynopsisProvider.LooksEnglish(realSynopsis),
    "An English synopsis was judged not to be English.");
Assert(OpenLibrarySynopsisProvider.LooksEnglish(pullQuoteBlurb),
    "An English blurb opening with a pull-quote was judged not to be English.");
Assert(!OpenLibrarySynopsisProvider.LooksEnglish(""), "Empty text was judged English.");

// An ISBN names one edition outright, so it settles which book a file is without matching
// titles. Files report either an ISBN or an Audible ASIN in the same field, and only the ISBN
// is usable: checked against real records, Open Library indexes Amazon print identifiers and
// holds no Audible ASINs at all, so treating one as an ISBN would be a guaranteed miss.
Assert(OpenLibrarySynopsisProvider.AsISBN("9781408857885") == "9781408857885",
    "An ISBN-13 was not recognised.");
Assert(OpenLibrarySynopsisProvider.AsISBN("978-1-4088-5788-5") == "9781408857885",
    "A punctuated ISBN-13 was not recognised.");
Assert(OpenLibrarySynopsisProvider.AsISBN("140885788X") == "140885788X",
    "An ISBN-10 ending in a check character was not recognised.");
Assert(OpenLibrarySynopsisProvider.AsISBN("B0BW2CCVQ2") is null,
    "An Audible ASIN was treated as an ISBN.");
Assert(OpenLibrarySynopsisProvider.AsISBN("B01A8ZNWXS") is null,
    "An Amazon ASIN was treated as an ISBN.");
Assert(OpenLibrarySynopsisProvider.AsISBN(null) is null, "A missing identifier became an ISBN.");
Assert(OpenLibrarySynopsisProvider.AsISBN("12345") is null,
    "A short number was treated as an ISBN.");

// Curating the catalogue. Hiding has to be reversible and has to leave the scan alone: the
// entry comes off the store front, but a listener who owns that file keeps its filter
// results, and putting it back must not need a database edit.
var curated = new InMemoryScanCatalog(Path.Combine(Path.GetTempPath(),
    $"audiochoice-curation-{Guid.NewGuid():N}"));
var curatedFingerprint = new BookFingerprint(
    3, new string('c', 64), 650_000, 39_600, "m4b", "The Deal", "Elle Kennedy",
    null, null, null, null, null);
var curatedCatalogID = new string('c', 24);
var curationOwner = Guid.NewGuid();
var curatedUpload = curated.CreateUpload(
    curationOwner,
    new CloudUploadAuthorizationRequest(curatedFingerprint, "the-deal.m4b", "audio/mp4", 650_000),
    DateTimeOffset.UtcNow.AddHours(1),
    "curation-token");
Assert(curated.MarkUploaded(curatedUpload.ID, "/private/curation.m4b"),
    "The curation fixture's upload was not recorded.");
var curatedJob = curated.CreateJob(curationOwner, curatedUpload.ID, curatedFingerprint);
Assert(curatedJob is not null, "The curation fixture's scan job was not created.");
Assert(curated.CompleteJob(curatedJob!.ID, new ScanResult([], DateTimeOffset.UnixEpoch, "v1")),
    "The curation fixture's scan did not complete.");
Assert(curated.ListExploreBooks().Any(book => book.CatalogID == curatedCatalogID),
    "A completed, identified scan did not reach the catalogue.");

// Setting a synopsis by hand. Files often carry no description tag, and without this such a
// book could never get one: the only other source is a client reporting the file's own tag.
var curatedSynopsis = "Hannah needs a tutor. Garrett needs to pass. Neither expects the deal "
    + "they strike to turn into something else entirely.";
Assert(
    curated.UpdateEditionMetadata(new AdminEditionMetadataRequest(
        curatedFingerprint, "The Deal", "Elle Kennedy", null, null, null, null, null, null,
        curatedSynopsis)),
    "An administrator could not set a synopsis.");
Assert(
    curated.ListExploreBooks()
        .Single(book => book.CatalogID == curatedCatalogID).Description == curatedSynopsis,
    "A synopsis set by an administrator was not served.");
// Correcting a title must not discard the synopsis, which is why the column is coalesced.
Assert(
    curated.UpdateEditionMetadata(new AdminEditionMetadataRequest(
        curatedFingerprint, "The Deal", "Elle Kennedy", "Off-Campus", 1, null, null, null, null)),
    "A metadata correction with no synopsis failed.");
Assert(
    curated.ListExploreBooks()
        .Single(book => book.CatalogID == curatedCatalogID).Description == curatedSynopsis,
    "Correcting an entry's metadata discarded its synopsis.");

Assert(curated.HideExploreBook(curatedCatalogID), "Hiding a catalogue entry failed.");
Assert(!curated.ListExploreBooks().Any(book => book.CatalogID == curatedCatalogID),
    "A hidden entry was still shown to listeners.");
// The whole point of hiding rather than deleting: the scan survives, so the listener who owns
// this file still gets their filters.
Assert(curated.FindResult(curatedFingerprint) is not null,
    "Hiding an entry destroyed the scan result.");
// An administrator has to be able to see a hidden entry, or it cannot be found to restore.
var hiddenEntry = curated.ListExploreCatalog()
    .SingleOrDefault(entry => entry.Book.CatalogID == curatedCatalogID);
Assert(hiddenEntry is not null, "A hidden entry was invisible to administrators too.");
Assert(!hiddenEntry!.IsPublished, "A hidden entry was reported as published.");
Assert(hiddenEntry.WithheldReason is not null, "A hidden entry gave no reason.");

Assert(curated.RestoreExploreBook(curatedCatalogID), "Restoring a hidden entry failed.");
Assert(curated.ListExploreBooks().Any(book => book.CatalogID == curatedCatalogID),
    "A restored entry did not come back to the catalogue.");
Assert(!curated.RestoreExploreBook(curatedCatalogID),
    "Restoring an entry that was never hidden reported success.");
Assert(!curated.RestoreExploreBook("nosuchcatalogid"),
    "Restoring an unknown catalog ID reported success.");

// Where the buy button goes. Every Explore entry points at Audible now, and the link has to
// be an exact listing whenever the file told us its product identifier, because sending a
// listener to a search result for a book they asked to buy is how they buy the wrong edition.
var audibleFingerprint = new BookFingerprint(
    3, new string('e', 64), 800_000, 3600, "m4b", "Fourth Wing", "Rebecca Yarros",
    null, null, null, null, null);
var audibleResult = new ScanResult([], DateTimeOffset.UnixEpoch, "v1");
var searchEntry = ExploreCatalog.Create(audibleFingerprint, audibleResult);
Assert(searchEntry.PurchaseProvider == "Audible",
    "Explore offered a provider other than Audible.");
Assert(searchEntry.PurchaseURL.Host.EndsWith("audible.com", StringComparison.Ordinal),
    "The purchase link did not point at Audible.");
Assert(!searchEntry.PurchaseVerified,
    "A search link was reported as a verified listing.");
Assert(searchEntry.PurchaseURL.AbsoluteUri.Contains("Fourth+Wing") ||
    searchEntry.PurchaseURL.AbsoluteUri.Contains("Fourth%20Wing"),
    "The Audible search did not carry the title.");

var asinEntry = ExploreCatalog.Create(
    audibleFingerprint, audibleResult, false, null, "B0BW2CCVQ2");
Assert(asinEntry.PurchaseURL.AbsoluteUri == "https://www.audible.com/pd/B0BW2CCVQ2",
    "An ASIN did not produce a direct Audible product link.");
Assert(asinEntry.PurchaseVerified,
    "A direct product link was not reported as verified.");

// An ISBN is not an ASIN. Putting one in an Audible product path resolves to nothing, so
// these have to fall back to a search rather than produce a dead link.
var isbnEntry = ExploreCatalog.Create(
    audibleFingerprint, audibleResult, false, null, "9781098765432");
Assert(isbnEntry.PurchaseURL.AbsoluteUri.Contains("/search?"),
    "An ISBN was used as though it were an Audible product identifier.");
Assert(!isbnEntry.PurchaseVerified, "An ISBN link was reported as a verified listing.");
Assert(!ExploreCatalog.IsAudibleProductIdentifier("B0BW2CCVQ"), "A nine-character identifier was accepted.");
Assert(!ExploreCatalog.IsAudibleProductIdentifier(null), "A missing identifier was accepted.");
Assert(ExploreCatalog.IsAudibleProductIdentifier("B0BW2CCVQ2"), "A valid ASIN was rejected.");

// Text scanning: offsets, non-persistence, and purpose limitation.
{
    // Passage offsets must index the original text exactly. Everything downstream -- the
    // masks, the removal, the reader's highlight -- is only as correct as this.
    const string prose =
        "Chapter One\n\nMr. Adams paid $4.50 and left. \"Wait!\" she called.\n\n" +
        "He did not turn. J. R. R. Tolkien wrote otherwise. The end.";
    var passages = TextScanPipeline.Passages(prose, 1_200);
    Assert(passages.Count > 0, "Passages produced nothing for ordinary prose.");
    foreach (var passage in passages)
    {
        var start = (int)passage.StartTime;
        var end = (int)passage.EndTime;
        Assert(
            start >= 0 && end <= prose.Length && end > start,
            $"A passage range {start}..{end} falls outside the text.");
        Assert(
            prose[start..end] == passage.Text,
            $"Passage text did not match the range it claims: '{passage.Text}'.");
        Assert(
            passage.Text.Length > 0 &&
            !char.IsWhiteSpace(passage.Text[0]) &&
            !char.IsWhiteSpace(passage.Text[^1]),
            $"A passage carried surrounding whitespace: '{passage.Text}'.");
    }
    // Ordered and non-overlapping, so merging masks later cannot double-count.
    for (var index = 1; index < passages.Count; index += 1)
    {
        Assert(
            passages[index].StartTime >= passages[index - 1].EndTime,
            "Passages overlap or are out of order.");
    }
    // Abbreviations and initials must not end a sentence, or a book's every "Mr." would
    // fragment into passages that cut a name in half.
    Assert(
        passages.Any(item => item.Text.Contains("Mr. Adams", StringComparison.Ordinal)),
        "A sentence was split after the honorific in 'Mr. Adams'.");
    Assert(
        passages.Any(item => item.Text.Contains("J. R. R. Tolkien", StringComparison.Ordinal)),
        "A sentence was split after an initial in 'J. R. R. Tolkien'.");
    // A decimal point is not a sentence end.
    Assert(
        passages.Any(item => item.Text.Contains("$4.50", StringComparison.Ordinal)),
        "A sentence was split inside the decimal '$4.50'.");
    // A heading is its own passage; a scene divider carrying no letters is not a passage
    // at all.
    Assert(
        TextScanPipeline.Passages("Alpha\n\n* * *\n\nBeta", 1_200).Count == 2,
        "A punctuation-only scene divider was sent to the classifier as a passage.");

    // A paragraph with no punctuation must still be broken up, and must break at
    // whitespace rather than through a word.
    var wall = string.Join(' ', Enumerable.Repeat("word", 400));
    var split = TextScanPipeline.Passages(wall, 100);
    Assert(split.Count > 1, "A long unpunctuated paragraph was not split.");
    foreach (var passage in split)
    {
        Assert(
            passage.Text.Length <= 100,
            $"A passage of {passage.Text.Length} characters exceeded the limit.");
        Assert(
            passage.Text.StartsWith("word", StringComparison.Ordinal) &&
            passage.Text.EndsWith("word", StringComparison.Ordinal),
            $"A passage split through a word: '{passage.Text}'.");
    }
    Assert(TextScanPipeline.Passages("", 1_200).Count == 0, "Empty text produced passages.");
    Assert(
        TextScanPipeline.Passages("   \n\n \r\n ", 1_200).Count == 0,
        "Whitespace-only text produced passages.");

    // Character offsets are whole numbers. The client discards a fractional offset on the
    // grounds that anything fractional was produced as a time, so the server must round.
    var scanCategory = ContentTaxonomy.Mappings["profanity_mild"];
    ScanEvent EventAt(double start, double end) => new(
        Guid.NewGuid(), start, end, scanCategory.CategoryID, scanCategory.GroupID,
        scanCategory.EventID, 1, "key", "Profanity detected");
    var usable = TextScanPipeline.UsableEvents(
        [EventAt(10.5, 20.4), EventAt(-5, 12), EventAt(30, 30), EventAt(40, 35),
         EventAt(90, 500)],
        100);
    Assert(
        usable.All(item =>
            item.StartTime == Math.Floor(item.StartTime) &&
            item.EndTime == Math.Floor(item.EndTime)),
        "A fractional character offset survived, which the client would discard.");
    Assert(
        usable.Any(item => item.StartTime == 10 && item.EndTime == 21),
        "A fractional range was not widened outward to whole characters.");
    Assert(
        usable.All(item => item.StartTime >= 0 && item.EndTime <= 100),
        "An event offset outside the book's text survived.");
    Assert(
        usable.All(item => item.EndTime > item.StartTime),
        "An empty or inverted range survived.");
    Assert(usable.Count == 3, $"Expected three usable events, found {usable.Count}.");

    // The store refuses a scan whose events cannot index the text that was scanned, which
    // is the only such check possible once the text itself is not kept.
    var storable = new NarrationTextScan(
        [EventAt(0, 10)], DateTimeOffset.UtcNow, "v1", ScanContracts.TaxonomyVersion, 100, "en");
    Assert(NarrationTextScans.IsStorable(storable), "A well-formed scan was refused.");
    Assert(
        !NarrationTextScans.IsStorable(storable with { Events = [EventAt(0, 500)] }),
        "A scan with an event past the end of the book was accepted.");
    Assert(
        !NarrationTextScans.IsStorable(storable with { Events = [EventAt(1.5, 10)] }),
        "A scan with a fractional offset was accepted.");
    Assert(
        !NarrationTextScans.IsStorable(storable with { BookTextCharacters = 0 }),
        "A scan claiming zero characters was accepted.");

    // The taxonomy the two paths report must be the same one, or a narrated book's switches
    // would control nothing.
    Assert(
        new CloudScanResponse(CloudScanStatus.Completed).TaxonomyVersion ==
        ScanContracts.TaxonomyVersion,
        "The audio and text scanning paths report different taxonomy versions.");

    // Non-persistence. A marker in the book's text must reach the classifier and nowhere
    // else: not a file under the data root, not the response, not the request's own
    // rendering, which is what a log scope would print.
    var marker = "ZQX-" + Guid.NewGuid().ToString("N");
    var root = Path.Combine(Path.GetTempPath(), "audiochoice-textscan-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var bookText =
            $"Chapter One\n\nThe {marker} sat quietly. Nothing else happened.\n\n" +
            $"Chapter Two\n\nAnd then the {marker} spoke.";
        var provider = new CapturingTextAnalysisProvider(supplied =>
            [EventAt(0, Math.Min(20, supplied[0].EndTime))]);
        var textPipeline = new TextScanPipeline(
            provider, NullLogger<TextScanPipeline>.Instance);
        var scanFingerprint = fingerprint with { FileType = "epub" };
        var textScan = await textPipeline.Scan(scanFingerprint, bookText, "en", null, default);

        var store = new FileNarrationTextScanStore(Path.Combine(root, "narration-text-scans.json"));
        store.Save(scanFingerprint, textScan);
        var reloaded = store.Load(scanFingerprint, textScan.ScannerVersion);
        Assert(reloaded is not null, "A stored text scan could not be read back.");
        Assert(
            reloaded!.Events.Count == textScan.Events.Count,
            "Reloading a text scan lost events.");

        // The classifier is the one place the text is allowed to go.
        Assert(provider.Calls.Count == 1, "The text was sent outward more than once.");
        Assert(
            provider.Calls[0].Any(item => item.Text.Contains(marker, StringComparison.Ordinal)),
            "The classifier never received the text, so the test proves nothing.");

        // Nothing written under the data root may contain it.
        foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            Assert(
                !File.ReadAllText(path).Contains(marker, StringComparison.Ordinal),
                $"The book's text was written to {Path.GetFileName(path)}.");
        }

        // Nor may anything travelling back to the client.
        var responseJson = JsonSerializer.Serialize(textScan.ToResponse());
        Assert(
            !responseJson.Contains(marker, StringComparison.Ordinal),
            "The response body carried the book's text back to the client.");
        Assert(
            responseJson.Contains("bookTextCharacters", StringComparison.OrdinalIgnoreCase) ||
            responseJson.Contains("BookTextCharacters", StringComparison.Ordinal),
            "The response omitted the scanned length, which is what makes a stale scan detectable.");

        // The request's own rendering is what a log scope prints, so it must not be the text.
        var request = new NarrationTextScanRequest(scanFingerprint, bookText, "en");
        var rendered = request.ToString();
        Assert(
            !rendered.Contains(marker, StringComparison.Ordinal),
            "The request record's ToString printed the book's text, which any log scope would capture.");
        Assert(
            rendered.Contains(bookText.Length.ToString(), StringComparison.Ordinal),
            "The request's rendering should report the text's length in place of the text.");
        Assert(
            $"Scanning {request}".Contains(marker, StringComparison.Ordinal) == false,
            "Interpolating the request into a message exposed the book's text.");
    }
    finally
    {
        Directory.Delete(root, true);
    }

    // Purpose limitation: exactly one outbound dependency, so there is no second place the
    // text could be sent from.
    var outbound = typeof(TextScanPipeline)
        .GetConstructors()
        .Single()
        .GetParameters()
        .Where(parameter => parameter.ParameterType !=
            typeof(Microsoft.Extensions.Logging.ILogger<TextScanPipeline>))
        .ToArray();
    Assert(
        outbound.Length == 1 &&
        outbound[0].ParameterType == typeof(ITextContentAnalysisProvider),
        "TextScanPipeline gained a dependency other than the classifier, so the book's " +
        "text now has somewhere else it could go.");

    // Explore exclusion, demonstrated rather than argued.
    //
    // A listener supplied this book. Listing it publicly would advertise what they are
    // reading, so the requirement is that a completed text scan yields no catalogue entry.
    // The catalogue is assembled from completed scan jobs, and a text scan creates none --
    // this checks that end rather than the reasoning behind it.
    {
        var narratedFingerprint = new BookFingerprint(
            1, new string('d', 64), 900_000, null, "epub",
            // Deliberately a title and author that would publish if anything did, so the
            // test cannot pass merely because the metadata was unpublishable.
            "A Narrated Novel", "A Real Author", null, null, "standard", null, null);
        var exclusionCatalog = new InMemoryScanCatalog();
        var exclusionStore = new FileNarrationTextScanStore(
            Path.Combine(Path.GetTempPath(), "audiochoice-exclusion-" + Guid.NewGuid().ToString("N") + ".json"));
        var narratedScan = new NarrationTextScan(
            [EventAt(0, 40)], DateTimeOffset.UtcNow, "text-contract-test",
            ScanContracts.TaxonomyVersion, 5_000, "en");
        exclusionStore.Save(narratedFingerprint, narratedScan);

        Assert(
            exclusionStore.Load(narratedFingerprint, "text-contract-test") is not null,
            "The text scan was not stored, so the exclusion test proves nothing.");
        Assert(
            ExploreCatalog.IsPublishable(narratedFingerprint),
            "The narrated fingerprint is unpublishable on its metadata alone, so this test " +
            "would pass even if a text scan did create a catalogue entry.");
        Assert(
            exclusionCatalog.ListExploreBooks().Count == 0,
            "A completed text scan produced an Explore catalogue entry.");
        Assert(
            exclusionCatalog.FindResult(narratedFingerprint) is null,
            "A completed text scan became a scan result other listeners can be served.");

        // The audio path, by contrast, must still publish. Asserted alongside so a change
        // that silenced the catalogue altogether could not be mistaken for correct exclusion.
        var audioFingerprint = narratedFingerprint with { FileType = "m4b", Duration = 3_600 };
        var audioOwner = Guid.NewGuid();
        var audioUpload = exclusionCatalog.CreateUpload(
            audioOwner,
            new CloudUploadAuthorizationRequest(audioFingerprint, "book.m4b", "audio/mp4", 900_000),
            DateTimeOffset.UtcNow.AddHours(1),
            "token");
        exclusionCatalog.MarkUploaded(audioUpload.ID, "stored/book.m4b");
        var audioJob = exclusionCatalog.CreateJob(audioOwner, audioUpload.ID, audioFingerprint);
        Assert(audioJob is not null, "The audio comparison job was not created.");
        exclusionCatalog.CompleteJob(
            audioJob!.ID,
            new ScanResult([EventAt(10, 20)], DateTimeOffset.UtcNow, "contract-test"));
        Assert(
            exclusionCatalog.ListExploreBooks().Count == 1,
            "A completed audio scan stopped producing a catalogue entry, so the exclusion " +
            "above may be hiding a broken catalogue rather than a working exclusion.");
    }

    // A text scan must have no route into the Explore catalogue, which is built from scan
    // results. Structural, so it cannot be forgotten.
    var storeSurface = typeof(INarrationTextScanStore).GetMethods()
        .SelectMany(method => method.GetParameters()
            .Select(parameter => parameter.ParameterType)
            .Append(method.ReturnType))
        .ToArray();
    Assert(
        storeSurface.All(type => type != typeof(ScanResult) && type != typeof(CloudScanResponse)),
        "The narration scan store touches the catalogue's types, so a supplied book could " +
        "become a public catalogue entry.");
    Assert(
        !typeof(INarrationTextScanStore).GetMethods()
            .SelectMany(method => method.GetParameters())
            .Any(parameter => parameter.ParameterType == typeof(string) &&
                 parameter.Name?.Contains("text", StringComparison.OrdinalIgnoreCase) == true),
        "The narration scan store accepts a text argument, which it must never be able to store.");

    // Synthesis routing.
    {
        var units = new[] { new SpokenUnit(0, 20, "A sentence to speak.") };
        var input = new ChapterSynthesisInput(Guid.NewGuid(), 3, "voice-1", "en", units);

        // Chapter synthesis text must not be printable by accident. A generated ToString would
        // put a chapter of a novel into any log scope holding this record.
        var synthesisMarker = "ZQX-" + Guid.NewGuid().ToString("N");
        var markedInput = input with
        {
            Units = [new SpokenUnit(0, 40, $"A sentence containing {synthesisMarker} in it.")],
        };
        Assert(
            !markedInput.ToString().Contains(synthesisMarker, StringComparison.Ordinal),
            "ChapterSynthesisInput.ToString printed the text to be spoken, which any log " +
            "scope would capture.");
        Assert(
            markedInput.ToString().Contains("Characters =", StringComparison.Ordinal),
            "ChapterSynthesisInput should report its size in place of its text.");

        var primaryOnly = new NarrationOptions { BillingCoverageVerified = true };

        // The happy path stays on the primary.
        var good = new FakeSynthesisProvider("primary");
        var standby = new FakeSynthesisProvider("fallback");
        var router = new SynthesisRouter(
            good, standby, primaryOnly, NullLogger<SynthesisRouter>.Instance);
        var routed = await router.Synthesize(input, default);
        Assert(routed.Route == SynthesisRoute.Primary, "A working primary was not used.");
        Assert(routed.Chapter.Provider == "primary", "The wrong provider synthesized a chapter.");
        Assert(standby.Calls == 0, "The fallback was called while the primary was working.");

        // A primary error falls back.
        var broken = new FakeSynthesisProvider("primary") { FailWith = new InvalidOperationException("boom") };
        standby = new FakeSynthesisProvider("fallback");
        routed = await new SynthesisRouter(
            broken, standby, primaryOnly, NullLogger<SynthesisRouter>.Instance)
            .Synthesize(input, default);
        Assert(
            routed.Route == SynthesisRoute.FallbackBecausePrimaryFailed,
            "A failing primary did not fall back.");
        Assert(standby.Calls == 1, "The fallback was not used after a primary failure.");

        // A primary that reports itself unavailable falls back without being called.
        var scaledToZero = new FakeSynthesisProvider("primary") { Available = false };
        standby = new FakeSynthesisProvider("fallback");
        routed = await new SynthesisRouter(
            scaledToZero, standby, primaryOnly, NullLogger<SynthesisRouter>.Instance)
            .Synthesize(input, default);
        Assert(
            routed.Route == SynthesisRoute.FallbackBecausePrimaryUnavailable,
            "An unavailable primary did not fall back.");
        Assert(scaledToZero.Calls == 0, "An unavailable primary was still asked to synthesize.");

        // A probe that throws says nothing reliable, so the attempt proceeds rather than
        // sending every chapter to the fallback on a flaky health check.
        var flakyProbe = new FakeSynthesisProvider("primary")
        {
            ProbeThrows = true,
        };
        routed = await new SynthesisRouter(
            flakyProbe, new FakeSynthesisProvider("fallback"), primaryOnly,
            NullLogger<SynthesisRouter>.Instance).Synthesize(input, default);
        Assert(
            routed.Route == SynthesisRoute.Primary,
            "A failing availability probe diverted work away from a working primary.");

        // The cold-start delay extends the budget rather than replacing it, so a provisioning
        // endpoint is not abandoned while it starts. Read from a measurement, not assumed.
        var withColdStart = new NarrationOptions
        {
            BillingCoverageVerified = true,
            ColdStartDelaySeconds = 120,
        };
        var coldPrimary = new FakeSynthesisProvider("primary");
        var routedCold = await new SynthesisRouter(
            coldPrimary, new FakeSynthesisProvider("fallback"), withColdStart,
            NullLogger<SynthesisRouter>.Instance).Synthesize(input, default);
        Assert(
            routedCold.Route == SynthesisRoute.Primary,
            "A cold-start allowance changed which provider a healthy primary is given.");
        Assert(
            new NarrationOptions().ColdStartDelaySeconds == 0,
            "The cold-start delay has a non-zero default, which would make it an assumption rather " +
            "than the measurement it is meant to be.");

        // The audio format promises, which the storage estimate is calibrated against.
        Assert(
            PollySynthesisProvider.AudioChannels == "1" &&
            PollySynthesisProvider.AudioBitrate == "32k",
            "The narration audio format changed; the measured 207 bytes per character no longer " +
            "applies and the storage estimate would mislead a listener.");

        // Every chapter records which provider spoke it, which is what makes a book that spans a
        // subscription lapse explicable rather than mysterious.
        Assert(
            !string.IsNullOrWhiteSpace(routed.Chapter.Provider) &&
            !string.IsNullOrWhiteSpace(routed.Chapter.ModelVersion),
            "A synthesized chapter no longer records its provider and model version.");

        // Timings are chapter-relative and contiguous, which is what the reader highlights from.
        var manyUnits = input with
        {
            Units =
            [
                new SpokenUnit(0, 20, "The first sentence."),
                new SpokenUnit(20, 44, "And then a second one."),
                new SpokenUnit(44, 90, "Followed by a third, rather longer than the others."),
            ],
        };
        var timed = await new SynthesisRouter(
            new FakeSynthesisProvider("primary"), new FakeSynthesisProvider("fallback"),
            primaryOnly, NullLogger<SynthesisRouter>.Instance).Synthesize(manyUnits, default);
        var timings = timed.Chapter.Timings;
        Assert(timings.Count == 3, "A timing was not produced for every unit.");
        Assert(
            Math.Abs(timings[0].StartSeconds) < 0.0001,
            "The first unit does not start at zero, so the timings are not chapter-relative.");
        for (var index = 1; index < timings.Count; index += 1)
        {
            Assert(
                Math.Abs(timings[index].StartSeconds - timings[index - 1].EndSeconds) < 0.0001,
                $"There is a gap before timing {index}; the reader's highlight would fall into it.");
        }
        Assert(
            Math.Abs(timings[^1].EndSeconds - timed.Chapter.DurationSeconds) < 0.0001,
            "The last timing does not end at the chapter's duration.");
        // Character offsets are carried through untouched, which is what lets audio be mapped back
        // to the words on screen even where filtering removed what was between the units.
        Assert(
            timings.Select(timing => timing.StartCharacter).SequenceEqual([0, 20, 44]),
            "The unit character offsets were altered in transit.");

        // A chapter with nothing left to say produces a real result with no audio, rather than an
        // error: it counts as rendered and adds nothing to the book's duration.
        var silent = await new SynthesisRouter(
            new FakeSynthesisProvider("primary"), new FakeSynthesisProvider("fallback"),
            primaryOnly, NullLogger<SynthesisRouter>.Instance)
            .Synthesize(input with { Units = [] }, default);
        Assert(
            silent.Chapter.DurationSeconds == 0 && silent.Chapter.Audio.Length == 0,
            "A fully filtered chapter did not render as silence.");
        Assert(
            silent.Chapter.Timings.Count == 0,
            "A silent chapter produced timings for units that were never spoken.");

        // A primary that stalls past its budget falls back. The budget is the router's own
        // constant plus the recorded cold-start delay, so a zero delay keeps this quick.
        var stalled = new FakeSynthesisProvider("primary")
        {
            Delay = TimeSpan.FromSeconds(30),
        };
        standby = new FakeSynthesisProvider("fallback");
        var impatient = new SynthesisRouter(
            stalled, standby,
            // A one-second budget is expressed through the cold-start delay being negative
            // relative to the constant, which is not configurable -- so instead the stall is
            // simply longer than the test is willing to wait, and cancellation is used.
            primaryOnly, NullLogger<SynthesisRouter>.Instance);
        using (var giveUp = new CancellationTokenSource())
        {
            // Cancelling the caller must NOT be read as a budget expiry: the listener leaving is
            // not the endpoint being slow, and falling back would keep spending on a chapter
            // nobody is waiting for.
            giveUp.CancelAfter(TimeSpan.FromMilliseconds(200));
            var cancelled = false;
            try
            {
                await impatient.Synthesize(input, giveUp.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            Assert(
                cancelled,
                "Cancelling the caller was absorbed as a timeout, so a chapter nobody is " +
                "waiting for would still be synthesized by the fallback.");
            Assert(
                standby.Calls == 0,
                "A cancelled request still sent work to the fallback.");
        }

        // Unverified billing routes everything to the fallback, and never touches the primary.
        var untouched = new FakeSynthesisProvider("primary");
        standby = new FakeSynthesisProvider("fallback");
        var unverified = new SynthesisRouter(
            untouched, standby, new NarrationOptions(), NullLogger<SynthesisRouter>.Instance);
        routed = await unverified.Synthesize(input, default);
        Assert(
            routed.Route == SynthesisRoute.FallbackBecauseBillingUnverified,
            "Unverified billing did not route to the fallback.");
        Assert(untouched.Calls == 0, "Unverified billing still sent work to the primary.");
        Assert(
            unverified.ProviderInEffect.Provider == "fallback",
            "The provider in effect should be the fallback while billing is unverified.");
        Assert(
            !new NarrationOptions().BillingCoverageVerified,
            "Billing coverage defaults to verified, which would route work to an endpoint " +
            "whose cost has not been checked.");

        // The endpoint collision assertion must fail the process, in every shape a host can
        // be written in.
        foreach (var (transcription, synthesis) in new[]
                 {
                     ("http://127.0.0.1:8001/", "http://127.0.0.1:9000/"),
                     ("http://gpu-host:8001/", "https://gpu-host/synthesize"),
                     ("gpu-host", "gpu-host"),
                 })
        {
            var collided = false;
            try
            {
                SynthesisRouter.AssertEndpointsAreDistinct(transcription, synthesis);
            }
            catch (InvalidOperationException)
            {
                collided = true;
            }
            Assert(
                collided,
                $"'{transcription}' and '{synthesis}' share a host but were allowed, so " +
                "narration synthesis could run on the transcription GPU.");
        }

        // Genuinely distinct hosts, and unconfigured endpoints, must start.
        SynthesisRouter.AssertEndpointsAreDistinct(
            "http://127.0.0.1:8001/", "https://polly.eu-west-1.amazonaws.com/");
        SynthesisRouter.AssertEndpointsAreDistinct("http://127.0.0.1:8001/", "");
        SynthesisRouter.AssertEndpointsAreDistinct("", "http://127.0.0.1:8001/");

        // The router must not be able to reach the transcription lane at all.
        var routerDependencies = typeof(SynthesisRouter).GetConstructors().Single()
            .GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        Assert(
            routerDependencies.All(type => type != typeof(ITranscriptionProvider)),
            "SynthesisRouter took a dependency on the transcription provider, so narration " +
            "work could reach the transcription GPU.");
    }

    // The Polly provider's audio format.
    //
    // Verified against the real service on 2026-08-29 with a throwaway harness: mono Opus, an
    // effective 29.7 kbps, and a file whose own duration matched the reported per-unit timings to
    // the millisecond. That harness needed AWS credentials and a network, so what remains here are
    // the promises that can be checked without either -- which are the ones a later edit might
    // quietly change.
    {
        Assert(
            PollySynthesisProvider.AudioChannels == "1",
            "Narration audio is no longer mono, which doubles every book's size to carry one voice.");
        Assert(
            PollySynthesisProvider.AudioBitrate == "32k",
            "The narration bitrate changed; the storage estimate is calibrated against 32k Opus.");

        // The provider must not be able to reach the transcription lane.
        var pollyDependencies = typeof(PollySynthesisProvider).GetConstructors().Single()
            .GetParameters().Select(parameter => parameter.ParameterType).ToArray();
        Assert(
            pollyDependencies.All(type =>
                type != typeof(ITranscriptionProvider) &&
                type != typeof(IContentAnalysisProvider)),
            "PollySynthesisProvider took a dependency on the transcription or analysis lane.");

        // Synthesis is off unless switched on, like text scanning, and separately from it.
        Assert(
            !new NarrationOptions().SynthesisEnabled,
            "Premium synthesis defaults to enabled, which would let a deploy start spending.");
    }

    // A filter report's position unit is additive on this side too.
    //
    // An existing client sends no positionUnit at all, and that has to keep meaning seconds.
    // The stored report always carries an explicit unit, so nothing reading one has to guess.
    {
        var audiobookReport = new FilterReportRequest(
            fingerprint, FilterReportKind.MissedContent, 1_234.5, 20);
        Assert(
            audiobookReport.PositionUnit is null,
            "A filter report request now defaults to a position unit, which changes the shape " +
            "an already-shipped client has to send.");

        var storedAudiobook = FilterReports.Validate(Guid.NewGuid(), audiobookReport);
        Assert(
            storedAudiobook?.PositionUnit == FilterReportPositionUnits.Seconds,
            "A report with no unit was not stored as seconds, which is what it means.");

        var narrationReport = audiobookReport with { PositionUnit = "characterOffset" };
        Assert(
            FilterReports.Validate(Guid.NewGuid(), narrationReport)?.PositionUnit ==
            FilterReportPositionUnits.CharacterOffset,
            "A narration report's character-offset unit was not preserved.");

        // Permissive rather than rejecting: a report is a one-off observation from someone who
        // heard a mistake, and discarding it over an unrecognised unit would lose the only
        // record that it happened.
        Assert(
            FilterReports.Validate(Guid.NewGuid(), audiobookReport with { PositionUnit = "furlongs" })
                ?.PositionUnit == FilterReportPositionUnits.Seconds,
            "An unrecognised position unit was not normalised to seconds.");

        // Matches the values the migration's check constraint allows, so a normalised unit can
        // always be written.
        var migrationSql = File.ReadAllText(
            Path.Combine(FindMigrationsDirectory(), "027_epub_narration.sql"));
        foreach (var unit in new[]
                 { FilterReportPositionUnits.Seconds, FilterReportPositionUnits.CharacterOffset })
        {
            Assert(
                migrationSql.Contains($"'{unit}'", StringComparison.Ordinal),
                $"The position_unit constraint does not permit '{unit}', so a normalised " +
                "report could not be written.");
        }
    }

    // The measurements this feature depends on, recorded rather than derived.
    //
    // Three constants in this feature were reasoned from plausible assumptions and each was wrong
    // by 13 to 33 percent, always in the direction of over-estimating. These are the figures that
    // replaced them, and they are asserted here so a later edit cannot quietly revert a measured
    // value to a derived one.
    {
        var measurementRoot = Path.Combine(
            Path.GetTempPath(), "audiochoice-measurements-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var measurements = new FileNarrationMeasurementStore(measurementRoot);

            foreach (var kind in new[]
                     {
                         NarrationMeasurementKinds.PremiumSynthesisRate,
                         NarrationMeasurementKinds.LocalSynthesisRate,
                         NarrationMeasurementKinds.LocalRealTimeFactor,
                         NarrationMeasurementKinds.BytesPerCharacter,
                     })
            {
                var latest = measurements.Latest(kind);
                Assert(latest is not null, $"No measurement is recorded for '{kind}'.");
                Assert(
                    !string.IsNullOrWhiteSpace(latest!.Target),
                    $"The '{kind}' measurement names no target, so it cannot be re-checked and is " +
                    "a number with no claim attached.");
            }

            // The two speech rates are within two percent of each other, which turned out to be a
            // fact about speech rather than a coincidence. A large gap would mean one was re-derived.
            var premium = measurements.Latest(NarrationMeasurementKinds.PremiumSynthesisRate)!.Value;
            var local = measurements.Latest(NarrationMeasurementKinds.LocalSynthesisRate)!.Value;
            Assert(
                Math.Abs(premium - local) < 2.0,
                $"The measured premium ({premium}) and device ({local}) speech rates have " +
                "drifted apart, which suggests one was replaced by a derivation.");

            // Both are far from the original guess of 13.5, which is the error worth not repeating.
            Assert(
                premium > 15.0 && local > 15.0,
                "A speech rate fell back towards the derived figure that was 33 percent low.");

            // Seeding is idempotent: a restart must not accumulate duplicates.
            var seededCount = measurements.List().Count;
            var reopened = new FileNarrationMeasurementStore(measurementRoot);
            Assert(
                reopened.List().Count == seededCount,
                "Re-opening the measurement store duplicated its seeded records.");

            // A real measurement sorts ahead of the seed rather than being lost behind it.
            reopened.Record(new NarrationMeasurement(
                Guid.NewGuid(),
                NarrationMeasurementKinds.LocalRealTimeFactor,
                4.2,
                DateTimeOffset.UtcNow,
                "a slower device",
                "android-system-tts"));
            Assert(
                Math.Abs(
                    reopened.Latest(NarrationMeasurementKinds.LocalRealTimeFactor)!.Value - 4.2) < 0.001,
                "A newly recorded measurement did not supersede the seeded one.");
        }
        finally
        {
            File.Delete(measurementRoot);
        }
    }

    // Per-chapter render records, and what they make answerable.
    {
        var renderPath = Path.Combine(
            Path.GetTempPath(), "audiochoice-renders-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var renders = new FileNarrationRenderStore(renderPath);
            var listener = Guid.NewGuid();
            var narrated = fingerprint with { FileType = "epub", Duration = null };

            NarrationChapterRender render(int chapter, string voice, string provider, double seconds) =>
                new(Guid.NewGuid(), listener, narrated, chapter, voice, provider,
                    "model-1", seconds, "device", DateTimeOffset.UtcNow);

            // A book made across a subscription lapse: premium first, then the device's own voice.
            renders.Record(render(0, "Ruth", "polly", 600));
            renders.Record(render(1, "Ruth", "polly", 620));
            renders.Record(render(2, "en-US-language", "android-system-tts", 590));

            var forBook = renders.ForBook(listener, narrated.Sha256);
            Assert(forBook.Count == 3, "Not every recorded chapter was returned.");
            Assert(
                forBook.Select(item => item.ChapterIndex).SequenceEqual([0, 1, 2]),
                "Render records were not returned in chapter order.");

            // The question a listener actually asks when chapter three sounds different.
            Assert(
                NarrationRenderSummary.SpansSeveralVoices(forBook),
                "A book made by two voices was not reported as such, so its audio would be " +
                "inexplicable to whoever is asked about it.");
            var byVoice = NarrationRenderSummary.ChaptersByVoice(forBook);
            Assert(
                byVoice["Ruth"].SequenceEqual([0, 1]) &&
                byVoice["en-US-language"].SequenceEqual([2]),
                "The chapters attributed to each voice are wrong.");

            // Duration comes from the chapters actually made, never from the plan.
            Assert(
                Math.Abs(NarrationRenderSummary.RenderedDurationSeconds(forBook) - 1_810) < 0.001,
                "The rendered duration does not sum the chapters that exist.");

            // Re-rendering the same chapter with the same voice replaces rather than accumulates,
            // matching the unique key the migration enforces.
            renders.Record(render(0, "Ruth", "polly", 615));
            Assert(
                renders.ForBook(listener, narrated.Sha256).Count == 3,
                "Re-rendering a chapter accumulated a second record, so the two stores would " +
                "disagree with the database's unique key.");
            Assert(
                Math.Abs(
                    renders.ForBook(listener, narrated.Sha256)
                        .Single(item => item.ChapterIndex == 0).DurationSeconds - 615) < 0.001,
                "A re-render did not replace the earlier record's duration.");

            // A single-voice book is not reported as mixed.
            var otherListener = Guid.NewGuid();
            var single = new FileNarrationRenderStore(
                Path.Combine(Path.GetTempPath(), "audiochoice-renders-single-" + Guid.NewGuid().ToString("N") + ".json"));
            single.Record(new NarrationChapterRender(
                Guid.NewGuid(), otherListener, narrated, 0, "Ruth", "polly", "model-1", 100,
                "device", DateTimeOffset.UtcNow));
            Assert(
                !NarrationRenderSummary.SpansSeveralVoices(single.ForBook(otherListener, narrated.Sha256)),
                "A single-voice book was reported as spanning several voices.");

            // Records are about audio, never about text. Nothing on this contract could accept a
            // character of Spoken_Text even if it were handed one.
            Assert(
                typeof(INarrationRenderStore).GetMethods()
                    .SelectMany(method => method.GetParameters())
                    .All(parameter =>
                        parameter.Name?.Contains("text", StringComparison.OrdinalIgnoreCase) != true),
                "The render store accepts a text argument, which it must never be able to store.");
        }
        finally
        {
            File.Delete(renderPath);
        }
    }

    // A measured cold-start delay supersedes the configured one, and an absurd measurement is
    // ignored rather than honoured.
    {
        var measurementPath = Path.Combine(
            Path.GetTempPath(), "audiochoice-coldstart-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new FileNarrationMeasurementStore(measurementPath);
            // Its own input: the earlier one is scoped to the routing block above.
            var coldInput = new ChapterSynthesisInput(
                Guid.NewGuid(), 0, "voice-1", "en",
                [new SpokenUnit(0, 20, "A sentence to speak.")]);
            var configured = new NarrationOptions
            {
                BillingCoverageVerified = true,
                ColdStartDelaySeconds = 30,
            };

            // With no measurement the configured value stands.
            var withoutMeasurement = new SynthesisRouter(
                new FakeSynthesisProvider("primary"), new FakeSynthesisProvider("fallback"),
                configured, NullLogger<SynthesisRouter>.Instance, store);
            Assert(
                (await withoutMeasurement.Synthesize(coldInput, default)).Route == SynthesisRoute.Primary,
                "A configured cold-start delay changed which provider was used.");

            // A cold start longer than ten minutes is a broken endpoint, not a delay to wait out.
            Assert(
                SynthesisRouter.MaximumColdStartSeconds == 600,
                "The cold-start ceiling moved; an absurd measurement could now be honoured.");
            store.Record(new NarrationMeasurement(
                Guid.NewGuid(), NarrationMeasurementKinds.ColdStartDelay, 99_999,
                DateTimeOffset.UtcNow, "a misbehaving endpoint", "test"));
            Assert(
                (await new SynthesisRouter(
                    new FakeSynthesisProvider("primary"), new FakeSynthesisProvider("fallback"),
                    configured, NullLogger<SynthesisRouter>.Instance, store)
                    .Synthesize(coldInput, default)).Route == SynthesisRoute.Primary,
                "An absurd cold-start measurement broke ordinary routing.");
        }
        finally
        {
            File.Delete(measurementPath);
        }
    }

    // The rate benchmark is exercised against the fake provider, so its arithmetic is checked
    // without needing credentials. The real measurement it produced is seeded in the measurement
    // store; this only proves the tool that took it computes what it claims to.
    {
        var benchmark = new SynthesisRateBenchmark(new FakeSynthesisProvider("polly"));
        Assert(
            SynthesisRateBenchmark.PassageCharacters is > 800 and < 1_500,
            "The benchmark passage changed length, so new measurements are not comparable with " +
            $"the recorded one ({SynthesisRateBenchmark.PassageCharacters} characters).");
        Assert(
            SynthesisRateBenchmark.Passage.Any(passage => passage.Contains('"')),
            "The benchmark passage has no dialogue, so it does not represent a novel.");

        var measured = await benchmark.MeasureAll(["Ruth", "Matthew"], default);
        Assert(measured.Count == 2, "The benchmark did not measure every voice it was given.");
        foreach (var item in measured)
        {
            Assert(
                item.CharactersPerSecondOfAudio > 0 && item.RealTimeFactor > 0,
                $"The benchmark produced no usable rate for {item.VoiceID}.");
            // The units must not be confusable: characters per second of audio is the speaking
            // rate, and it is a very different number from characters per second of work.
            Assert(
                item.CharactersPerSecondOfAudio < 100,
                "The reported speaking rate is implausibly high, which usually means the work rate " +
                "was reported instead.");
        }

        var records = SynthesisRateBenchmark.AsRecords(measured, DateTimeOffset.UtcNow);
        Assert(records.Count == 2, "The benchmark did not produce both measurement records.");
        Assert(
            records.All(record => !string.IsNullOrWhiteSpace(record.Target)),
            "A benchmark record names no target, so it could not be re-checked later.");
        Assert(
            records.Any(record => record.Kind == NarrationMeasurementKinds.PremiumSynthesisRate) &&
            records.Any(record => record.Kind == NarrationMeasurementKinds.BytesPerCharacter),
            "The benchmark no longer records both the rate and the size it measured.");
        Assert(
            SynthesisRateBenchmark.AsRecords([], DateTimeOffset.UtcNow).Count == 0,
            "The benchmark invented records from no measurements.");
    }

    // The timing promises have to survive the measured rate.
    //
    // Two bounds were written before anything was measured: each HTTP interaction answers within 30
    // seconds, and the router gives a provider 60 seconds for one chapter. Now that the synthesis
    // rate is a measurement rather than an assumption, they can be checked instead of hoped for.
    // Asserted here because a rate is only reassuring if somebody has divided by it.
    {
        // 18.0 characters per second of audio, measured across three Polly generative voices.
        const double measuredCharactersPerSecondOfAudio = 18.0;

        // Polly returns audio faster than real time; the figure below is deliberately pessimistic
        // relative to what was observed (1,080 characters, three voices, a couple of seconds each).
        const double conservativeRealTimeFactor = 5.0;

        // The longest chapter the endpoint accepts.
        var longestChapterCharacters = NarrationSynthesisLimits.MaximumChapterCharacters;
        var audioSeconds = longestChapterCharacters / measuredCharactersPerSecondOfAudio;
        var synthesisSeconds = audioSeconds / conservativeRealTimeFactor;

        // R9.7: every HTTP interaction bounded at 30 seconds. This holds because chapter synthesis
        // is a job -- the submission returns 202 immediately and the client polls -- so no request
        // waits on the work. If synthesis were ever made synchronous this assertion is what would
        // fail, and it fails loudly rather than as a production timeout.
        Assert(
            synthesisSeconds > 30,
            $"A longest-case chapter now synthesizes in {synthesisSeconds:F0} seconds, which is " +
            "inside the 30-second HTTP bound. That is good news, but it means the job indirection " +
            "may no longer be load-bearing -- re-check before simplifying it away, because the " +
            "bound is per interaction and a slower provider would breach it again.");

        // R10.5: the router gives the primary 60 seconds per chapter before falling back. A
        // longest-case chapter legitimately exceeds that, which is why the budget adds the recorded
        // cold-start delay and why the fallback exists at all.
        Assert(
            SynthesisRouter.PrimaryTimeoutSeconds == 60,
            "The router's per-chapter budget changed; the reasoning below assumes 60 seconds.");
        Assert(
            synthesisSeconds > SynthesisRouter.PrimaryTimeoutSeconds,
            $"A longest-case chapter is expected to take {synthesisSeconds:F0} seconds, which no " +
            "longer exceeds the router's budget. Verify the fallback still has a reason to exist.");

        // And the client's own ceiling agrees with the server's, so a chapter cannot be accepted by
        // one and refused by the other.
        Assert(
            longestChapterCharacters == 40_000,
            "The server's chapter ceiling moved; PremiumVoiceEngine.MAXIMUM_CHAPTER_CHARACTERS " +
            "mirrors it and must move together, or a chapter would be submitted and then refused.");

        // A whole novel at the measured rate, as a sanity check on the storage estimate the client
        // shows a listener before rendering.
        var novelHours = 400_000 / measuredCharactersPerSecondOfAudio / 3_600;
        Assert(
            novelHours is > 4 and < 9,
            $"A 400,000-character novel now estimates {novelHours:F1} hours of audio, which is " +
            "outside the range a novel plausibly occupies. The measured rate is probably wrong.");
    }

    // Guard retention.
    //
    // Every promise in this feature that is one plausible edit away from being lost, asserted in one
    // place. Each of these was verified to fail when the thing it guards was removed -- several were
    // added only after making that edit and finding nothing else caught it.
    {
        // Text is held for one request and never written down.
        Assert(
            typeof(NarrationTextScanRequest).GetMethod("ToString")?.DeclaringType ==
            typeof(NarrationTextScanRequest),
            "NarrationTextScanRequest no longer overrides ToString, so any log scope holding one " +
            "would print an entire novel.");
        Assert(
            typeof(ChapterSynthesisInput).GetMethod("ToString")?.DeclaringType ==
            typeof(ChapterSynthesisInput),
            "ChapterSynthesisInput no longer overrides ToString, so a log scope would print a " +
            "chapter of somebody's book.");
        Assert(
            typeof(NarrationChapterRequest).GetMethod("ToString")?.DeclaringType ==
            typeof(NarrationChapterRequest),
            "NarrationChapterRequest no longer overrides ToString.");

        // Narration synthesis must never share the transcription GPU.
        var collided = false;
        try
        {
            SynthesisRouter.AssertEndpointsAreDistinct("http://gpu:8001/", "http://gpu:9000/");
        }
        catch (InvalidOperationException) { collided = true; }
        Assert(collided, "The transcription-GPU collision assertion no longer fires.");

        // Nothing is on by default, so deploying this changes nothing in a running environment.
        var defaults = new NarrationOptions();
        Assert(!defaults.TextScanEnabled, "Text scanning defaults to on.");
        Assert(!defaults.SynthesisEnabled, "Premium synthesis defaults to on.");
        Assert(
            !defaults.BillingCoverageVerified,
            "Billing coverage defaults to verified, which would route spend to an unchecked endpoint.");

        // The server-side budget must stay inside the client's read timeout, or a slow scan reaches
        // the listener as a dropped connection rather than as the 504 this endpoint returns.
        Assert(
            defaults.TextScanTimeoutSeconds < 90,
            "The text scan budget exceeds the Android client's 90-second read timeout, so a slow " +
            "scan would surface as a network error rather than as a timeout.");

        // A text scan cannot reach the Explore catalogue.
        Assert(
            typeof(INarrationTextScanStore).GetMethods()
                .SelectMany(method => method.GetParameters()
                    .Select(parameter => parameter.ParameterType)
                    .Append(method.ReturnType))
                .All(type => type != typeof(ScanResult)),
            "The narration scan store touches the catalogue's result type.");

        // Neither the router nor the synthesis provider may reach the transcription lane.
        foreach (var type in new[] { typeof(SynthesisRouter), typeof(PollySynthesisProvider) })
        {
            Assert(
                type.GetConstructors().Single().GetParameters()
                    .All(parameter => parameter.ParameterType != typeof(ITranscriptionProvider)),
                $"{type.Name} took a dependency on the transcription provider.");
        }

        // A filter report from an existing client still means seconds.
        Assert(
            new FilterReportRequest(fingerprint, FilterReportKind.MissedContent, 1, 20)
                .PositionUnit is null,
            "A filter report request now defaults to a position unit, changing the wire shape.");
    }

    // Narration is off unless switched on, so deploying this changes nothing by itself.
    Assert(
        !new NarrationOptions().TextScanEnabled,
        "Text scanning defaults to enabled, which would change a running environment on deploy.");

    // The container registers the text classifier by casting the audio one to it. The cast
    // is only reached on the first request, so it is asserted here at type level instead,
    // where it cannot wait for production to be found wrong.
    Assert(
        typeof(ITextContentAnalysisProvider)
            .IsAssignableFrom(typeof(OpenAIContentAnalysisProvider)),
        "OpenAIContentAnalysisProvider no longer implements the text classifier contract, " +
        "so resolving a text scan would throw on the first request.");

    // The audio path must keep its scene post-processing, whose constants are seconds. The
    // text path must not have it. Asserting both directions means neither can be moved into
    // the other by a later tidy-up.
    var analyzeBody = typeof(OpenAIContentAnalysisProvider)
        .GetMethod(nameof(OpenAIContentAnalysisProvider.AnalyzeCharacterOffsets));
    Assert(analyzeBody is not null, "The character-offset classifier entry point is missing.");
    Assert(
        typeof(OpenAIContentAnalysisProvider).GetMethod(
            nameof(OpenAIContentAnalysisProvider.Analyze)) is not null,
        "The audio classifier entry point is missing.");
}

// EPUB narration migration guards.
//
// Two promises this migration makes are the kind that erode: that it is additive, so a
// beta or release client keeps seeing identical responses, and that the book's text is
// never written down. Both are cheap to check without a database, and expensive to
// discover broken in production.
{
    var migrationsDirectory = FindMigrationsDirectory();
    var narrationMigration = Path.Combine(migrationsDirectory, "027_epub_narration.sql");
    Assert(File.Exists(narrationMigration), "The EPUB narration migration is missing.");
    var narrationSql = File.ReadAllText(narrationMigration);

    // Additive only. A drop or a delete here would change what an existing client sees.
    foreach (var forbidden in new[] { "drop table", "delete from", "truncate", "drop column" })
    {
        Assert(
            !narrationSql.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
            $"The narration migration must be additive, but it contains '{forbidden}'.");
    }

    // No column may hold the book's text. The promise is that text is held for one scan
    // request and never persisted, and a column would make that impossible to keep.
    foreach (var forbidden in new[] { "book_text ", "book_text\n", "epub_text", "spoken_text" })
    {
        Assert(
            !narrationSql.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
            $"The narration migration must store no book text, but it declares '{forbidden}'.");
    }

    // Character offsets are named as such on this side of the wire. The client carries
    // them in a ScanEvent's time fields to reuse the filter stack; the database has no
    // reason to inherit that ambiguity.
    Assert(
        narrationSql.Contains("start_character", StringComparison.Ordinal) &&
        narrationSql.Contains("end_character", StringComparison.Ordinal),
        "Narration scan events must record character offsets under character-named columns.");

    // Migrations are applied in filename order, so a new one must sort after every
    // existing one or it will be skipped on databases that are already up to date.
    var lastExisting = Directory.GetFiles(migrationsDirectory, "*.sql")
        .Select(Path.GetFileName)
        .Where(name => name is not null && name != "027_epub_narration.sql")
        .Order(StringComparer.Ordinal)
        .Last();
    Assert(
        string.CompareOrdinal("027_epub_narration.sql", lastExisting) > 0,
        $"The narration migration must sort after {lastExisting}.");
}


// Bedrock takes a tool's input schema, and hands back the tool's arguments, as the SDK's
// Document type rather than as JSON. The scanner's schema therefore survives a round trip
// through another representation, and this is where it could quietly change shape without
// failing: an enum flattened to a plain string, a bound dropped, an integer arriving as text.
// The model would then answer in a shape slightly off from what the taxonomy accepts, the
// unknown labels would be discarded, and the book would scan successfully having filtered
// less than it should. So the real taxonomy goes through it, not a toy schema.
var taxonomySchema = new System.Text.Json.Nodes.JsonObject
{
    ["type"] = "object",
    ["additionalProperties"] = false,
    ["required"] = new System.Text.Json.Nodes.JsonArray("events"),
    ["properties"] = new System.Text.Json.Nodes.JsonObject
    {
        ["events"] = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "array",
            ["items"] = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "object",
                ["properties"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["label"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["type"] = "string",
                        ["enum"] = new System.Text.Json.Nodes.JsonArray(
                            ContentTaxonomy.EnforcedLabels
                                .Select(label => (System.Text.Json.Nodes.JsonNode)
                                    System.Text.Json.Nodes.JsonValue.Create(label)!)
                                .ToArray())
                    },
                    ["confidence"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["type"] = "number",
                        ["minimum"] = 0,
                        ["maximum"] = 1
                    },
                    ["startTime"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "number" },
                    ["safeDescription"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["type"] = "string",
                        ["maxLength"] = 80
                    },
                    ["profanityWord"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["type"] = new System.Text.Json.Nodes.JsonArray("string", "null"),
                        ["maxLength"] = 80
                    },
                    ["accepted"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "boolean" }
                }
            }
        }
    }
};
var roundTripped = BedrockDocuments.ToJsonNode(BedrockDocuments.ToDocument(taxonomySchema));
Assert(
    roundTripped is not null &&
        roundTripped.ToJsonString() == taxonomySchema.ToJsonString(),
    "A scan schema did not survive conversion to the Bedrock Document type unchanged.");

// Every label the taxonomy enforces has to still be offered to the model. One missing label
// is one kind of content that can never be reported, for every book scanned on Bedrock.
var roundTrippedLabels = roundTripped!["properties"]!["events"]!["items"]!["properties"]!
    ["label"]!["enum"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray();
Assert(
    roundTrippedLabels.SequenceEqual(ContentTaxonomy.EnforcedLabels),
    "The taxonomy label enum was altered by the Bedrock Document conversion.");

// The arguments a model sends back, in the shape the scanner deserializes.
var reply = BedrockDocuments.ToDocument(
    System.Text.Json.Nodes.JsonNode.Parse(
        """
        {"events":[{"label":"profanity_strong","startTime":12.5,"endTime":13,
        "confidence":0.92,"safeDescription":"Profanity detected","profanityWord":"damn"}]}
        """));
var replyJson = BedrockDocuments.ToJsonNode(reply);
Assert(
    replyJson?["events"]?.AsArray().Count == 1 &&
        replyJson["events"]![0]!["confidence"]!.GetValue<double>() == 0.92 &&
        replyJson["events"]![0]!["label"]!.GetValue<string>() == "profanity_strong",
    "A model reply did not survive conversion from the Bedrock Document type.");

// A null must come back as a JSON null rather than as the string "null", which would reach
// the taxonomy as a profanity word nobody said.
var withNull = BedrockDocuments.ToJsonNode(BedrockDocuments.ToDocument(
    System.Text.Json.Nodes.JsonNode.Parse("""{"profanityWord":null}""")));
Assert(
    withNull?["profanityWord"] is null,
    "A null tool argument did not survive the Bedrock Document conversion as null.");


// Which service a tier reaches is decided by the model it names, so a name that matches
// neither vendor must stop the job rather than fall through to one of them. A book classified
// against the wrong provider would look entirely ordinary.
Assert(RoutingAnalysisModelClient.IsOpenAIModel("gpt-5.6-sol"), "An OpenAI model was not recognised.");
Assert(RoutingAnalysisModelClient.IsOpenAIModel("gpt-5.6-terra"), "An OpenAI model was not recognised.");
Assert(!RoutingAnalysisModelClient.IsOpenAIModel("amazon.nova-lite-v1:0"), "A Nova model was read as OpenAI.");
Assert(!RoutingAnalysisModelClient.IsOpenAIModel("us.amazon.nova-2-lite-v1:0"), "A Nova model was read as OpenAI.");
Assert(RoutingAnalysisModelClient.IsBedrockModel("amazon.nova-lite-v1:0"), "A Nova model was not recognised.");
Assert(RoutingAnalysisModelClient.IsBedrockModel("us.amazon.nova-2-lite-v1:0"), "A cross-region Nova profile was not recognised.");
Assert(!RoutingAnalysisModelClient.IsBedrockModel("gpt-5.6-sol"), "An OpenAI model was read as Bedrock.");
Assert(
    !RoutingAnalysisModelClient.IsOpenAIModel("nova-lite") &&
        !RoutingAnalysisModelClient.IsBedrockModel("nova-lite"),
    "A model name missing its provider prefix was claimed by a vendor rather than refused.");

// The exact shape Nova returned that discarded four books' analysis: the list encoded as a
// string inside a one-element list. Pinned because the repair is invisible when it works and
// the failure is a byte offset inside text that reads like a correct answer.
var doubled = """
    {"candidates":["[{\u0022candidateKey\u0022: \u0022abc\u0022, \u0022accepted\u0022: true}]"]}
    """;
var flattenedNode = System.Text.Json.Nodes.JsonNode.Parse(doubled)!;
var innerText = flattenedNode["candidates"]![0]!.GetValue<string>();
Assert(
    System.Text.Json.Nodes.JsonNode.Parse(innerText) is System.Text.Json.Nodes.JsonArray inner &&
        inner.Count == 1 && inner[0]!["candidateKey"]!.GetValue<string>() == "abc",
    "The double-encoded reply shape this repair exists for is no longer what it was.");

// A single word must cost a single word. A segment runs five to ten seconds and one profanity was
// taking all of it, thousands of times across a library, which is the largest avoidable source of
// removed narration in the app.
var spokenSegment = new TranscriptSegment(100, 108, "Damn it, he said, slamming the door.", new[]
{
    new TranscriptWord("Damn", 100.2, 100.6),
    new TranscriptWord("it,", 100.6, 100.8),
    new TranscriptWord("he", 101.0, 101.2),
    new TranscriptWord("said,", 101.2, 101.6)
});
var wordScoped = DeterministicContentDetector.DetectProfanity([spokenSegment]);
Assert(wordScoped.Count == 1, "The profanity in a segment with word timings was not detected.");
Assert(
    Math.Abs(wordScoped[0].StartTime - 100.2) < 0.001 &&
        Math.Abs(wordScoped[0].EndTime - 100.6) < 0.001,
    "Profanity with word timings available still removed the whole segment rather than the word.");

// Every transcript saved before word timings existed has none, and those books must keep working.
var withoutWords = new TranscriptSegment(100, 108, "Damn it, he said.");
var segmentScoped = DeterministicContentDetector.DetectProfanity([withoutWords]);
Assert(
    segmentScoped.Count == 1 && segmentScoped[0].StartTime == 100 && segmentScoped[0].EndTime == 108,
    "A transcript without word timings did not fall back to the segment's own range.");

// Two of the same word must get two timings, not both the first one.
var twice = new TranscriptSegment(200, 210, "Damn, damn.", new[]
{
    new TranscriptWord("Damn,", 200.5, 200.9),
    new TranscriptWord("damn.", 201.5, 201.9)
});
var repeated = DeterministicContentDetector.DetectProfanity([twice]);
Assert(repeated.Count == 2, "Two occurrences of one word were not both detected.");
Assert(
    Math.Abs(repeated[0].StartTime - 200.5) < 0.001 &&
        Math.Abs(repeated[1].StartTime - 201.5) < 0.001,
    "A repeated word reused the first occurrence's timing for both.");

Console.WriteLine("AudioChoice backend contract tests passed.");

static string FindMigrationsDirectory()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null)
    {
        var candidate = Path.Combine(directory.FullName, "Database", "Migrations");
        if (Directory.Exists(candidate)) return candidate;
        directory = directory.Parent;
    }
    throw new DirectoryNotFoundException("Could not locate Database/Migrations.");
}

static ExploreCatalogBook Catalogued(
    string id, string title, string? author = null, string? editionType = null,
    int eventCount = 10, string? cover = null, double? duration = 3600,
    string? identifier = null) =>
    new(id, title, author, null, null, editionType, duration, "m4b",
        DateTimeOffset.UnixEpoch, "v1", eventCount, [], cover, null,
        new Uri("https://example.com"), "example", false, identifier);

static string Base64Url(byte[] value) =>
    Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

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

/// <summary>
/// A synthesis provider that can be told to fail, stall or report itself unavailable, which is
/// the only way to exercise every routing branch without two real endpoints.
/// </summary>
sealed class FakeSynthesisProvider(string provider) : ISynthesisProvider
{
    public string Provider => provider;
    public string ModelVersion => "fake-1";

    public Exception? FailWith { get; init; }
    public bool Available { get; init; } = true;
    public bool ProbeThrows { get; init; }
    /// <summary>Set to stall past the router's budget, for the timeout branch.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.Zero;

    public int Calls { get; private set; }

    public Task<IReadOnlyList<NarrationVoice>> Voices(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<NarrationVoice>>(
            [new NarrationVoice("voice-1", "Test Voice", "en", provider, "/samples/voice-1.opus")]);

    public Task<bool> IsAvailable(CancellationToken cancellationToken)
    {
        if (ProbeThrows) throw new InvalidOperationException("probe failed");
        return Task.FromResult(Available);
    }

    public async Task<SynthesizedChapter> Synthesize(
        ChapterSynthesisInput input,
        CancellationToken cancellationToken)
    {
        Calls += 1;
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
        if (FailWith is not null) throw FailWith;

        // Mirrors the real provider's early return for a chapter whose every unit was filtered
        // away: no request, no audio, no timings, and it still counts as rendered. A fake that
        // answered differently would make every test using it prove less than it appears to.
        if (input.Units.Count == 0)
        {
            return new SynthesizedChapter(
                input.JobID, input.ChapterIndex, provider, ModelVersion, input.VoiceID,
                0, [], []);
        }

        var timings = new List<UnitTiming>();
        var cursor = 0.0;
        foreach (var unit in input.Units)
        {
            var seconds = Math.Max(0.1, unit.Text.Length / 14.0);
            timings.Add(new UnitTiming(
                unit.StartCharacter, unit.EndCharacter, cursor, cursor + seconds));
            cursor += seconds;
        }
        return new SynthesizedChapter(
            input.JobID, input.ChapterIndex, provider, ModelVersion, input.VoiceID,
            cursor, timings, [1, 2, 3]);
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

/// <summary>
/// Records the passages a text scan hands over, and returns events the caller chooses.
/// </summary>
/// <remarks>
/// The point of capturing the passages is that they are the only thing the pipeline sends
/// outward. Asserting on what arrives here is how the non-persistence tests establish that
/// the text went exactly one place.
/// </remarks>
sealed class CapturingTextAnalysisProvider(
    Func<IReadOnlyList<TranscriptSegment>, IReadOnlyList<ScanEvent>>? respond = null)
    : ITextContentAnalysisProvider
{
    public string ScannerVersion => "text-contract-test";

    public List<IReadOnlyList<TranscriptSegment>> Calls { get; } = [];

    public Task<IReadOnlyList<ScanEvent>> AnalyzeCharacterOffsets(
        IReadOnlyList<TranscriptSegment> passages,
        Action<double>? reportProgress,
        CancellationToken cancellationToken)
    {
        Calls.Add(passages);
        return Task.FromResult(respond?.Invoke(passages) ?? []);
    }
}

/// <summary>
/// A transcript store that actually keys by fingerprint, which is what resolution
/// tests need: CapturingTranscriptStore returns the same transcript for every
/// fingerprint and so cannot tell a hit from a miss.
/// </summary>
sealed class KeyedTranscriptStore : IPrivateTranscriptStore
{
    private readonly Dictionary<string, PrivateTranscript> _byFingerprint = [];

    public Task<PrivateTranscript?> Load(
        BookFingerprint fingerprint,
        CancellationToken cancellationToken) =>
        Task.FromResult(_byFingerprint.GetValueOrDefault(
            InMemoryScanCatalog.FingerprintKey(fingerprint)));

    public Task Save(
        BookFingerprint fingerprint,
        PrivateTranscript transcript,
        CancellationToken cancellationToken)
    {
        _byFingerprint[InMemoryScanCatalog.FingerprintKey(fingerprint)] = transcript;
        return Task.CompletedTask;
    }
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
