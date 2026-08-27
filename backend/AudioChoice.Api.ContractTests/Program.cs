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
