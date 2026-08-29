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

Console.WriteLine("AudioChoice backend contract tests passed.");

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
