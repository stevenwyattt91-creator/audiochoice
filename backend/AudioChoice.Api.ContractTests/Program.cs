using System.Text;
using System.Text.Json;
using AudioChoice.Api.Contracts;
using AudioChoice.Api.Processing;
using AudioChoice.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
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

// The known-work catalogue: one canonical name for every way a file's own tags or
// filename might spell it, replacing what used to be a single hardcoded sha256 check.
Assert(
    KnownWorkCatalog.FindByIdentifier("9798890551030")?.CanonicalTitle == "Fourth Wing",
    "A known ASIN did not resolve to its work.");
Assert(
    KnownWorkCatalog.FindByIdentifier("978-9890551030") is null,
    "An unrelated identifier resolved to a known work.");
Assert(
    KnownWorkCatalog.FindByTitle("A Court of Thorns and Roses [Dramatized Adaptation] (Part 1 of 2)")
        ?.CanonicalTitle == "A Court of Thorns and Roses",
    "A title with part and edition wording did not resolve by title.");
Assert(
    KnownWorkCatalog.FindByTitle(
        "ACourtofThornsandRosesDramatizedAdaptationACourtofThornsandRosesBook1 ep7 (Part 1 of 2)")
        ?.CanonicalTitle == "A Court of Thorns and Roses",
    "A run-together EPUB-derived title did not resolve to its known work.");
Assert(
    KnownWorkCatalog.FindByTitle("A Court of Mist and Fury: A Court of Thorns and Roses, Book 2 (Part 2 of 2)")
        ?.CanonicalTitle == "A Court of Mist and Fury",
    "A series-qualified title resolved to the wrong book in its own series.");
Assert(
    KnownWorkCatalog.FindByTitle("Some Entirely Unrelated Audiobook") is null,
    "An unrelated title matched a known work.");

Assert(
    EditionTitleFormatter.Format(new BookFingerprint(
        1, new string('e', 64), 1_000, 21_846.68, "m4a",
        "1 A Court of Thorns and Roses [Dramatized Adaptation] (Dramatized) (Part 1 of 2) "
            + "(Dramatized Adaptation)",
        "Sarah J. Maas", null, null, null, null, null))
        == "A Court of Thorns and Roses (Part 1 of 2) (Dramatized Adaptation)",
    "A messy real-world ACOTAR title was not normalized to its canonical name.");
Assert(
    EditionTitleFormatter.Format(new BookFingerprint(
        1, new string('f', 64), 1_000, 21_107.5, "m4b",
        "A Court of Thorns and Roses (2 of 2)", "Sarah J. Maas", null, null, null, null, null))
        == "A Court of Thorns and Roses (Part 2 of 2) (Dramatized Adaptation)",
    "An abbreviated '(2 of 2)' title was not resolved to the known work's part and total.");
Assert(
    EditionTitleFormatter.Format(new BookFingerprint(
        1, new string('g', 64), 1_000, 93_855, "audio",
        "King Sorrow", "Joe Hill", "King Sorrow", null, null, null, null),
        productIdentifier: "B0DSCGNTXS")
        == "King Sorrow",
    "A known single-part work gained part wording it does not have.");

// A scan with no author tag but a title specific enough to name a known book must not be
// withheld from Explore for exactly the reason the title already answers.
var acotarNoAuthor = new BookFingerprint(
    1, new string('h', 64), 1_000, 21_846.68, "m4b",
    "ACourtofThornsandRosesDramatizedAdaptationACourtofThornsandRosesBook1 ep7 (Part 1 of 2)",
    null, null, null, null, null, null);
Assert(
    ExploreCatalog.IsPublishable(acotarNoAuthor),
    "A known work with no author tag was withheld as if its title had been guessed.");
Assert(
    ExploreCatalog.Create(acotarNoAuthor, result).Author == "Sarah J. Maas",
    "A known work's author was not supplied for a catalogue entry whose own tag was blank.");
Assert(
    !ExploreCatalog.IsPublishable(acotarNoAuthor with
    {
        WorkTitle = "Some Entirely Unrelated Audiobook With A Real Title But No Author",
    }),
    "An unknown book with no author tag was published; the author gate must still apply "
        + "to titles KnownWorkCatalog cannot vouch for.");

// End to end: the exact messy real-world titles seen live on staging, once canonicalized
// by the title formatter, must collapse into one Explore entry rather than several -- this
// is what makes the fix visible to a listener without a database migration, since
// Deduplicate groups on the formatted title and runs at read time on every request.
var messyAcotarPart2Variant1 = new BookFingerprint(
    1, new string('i', 64), 1_000, 21_107.5, "m4b",
    "A Court of Thorns and Roses (2 of 2)", "Sarah J. Maas", null, null, null, null, null);
var messyAcotarPart2Variant2 = new BookFingerprint(
    1, new string('j', 64), 1_000, 21_107.537, "m4a",
    "A Court of Thorns and Roses: A Court of Thorns and Roses, Book 1 (Part 2 of 2) (Dramatized)",
    "Sarah J. Maas", null, null, null, null, null);
var messyAcotarResult = new ScanResult(
    [new ScanEvent(Guid.NewGuid(), 1, 2, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1)],
    DateTimeOffset.UtcNow, "test");
var mergedAcotarView = ExploreCatalog.Deduplicate([
    ExploreCatalog.Create(messyAcotarPart2Variant1, messyAcotarResult),
    ExploreCatalog.Create(messyAcotarPart2Variant2, messyAcotarResult),
]);
Assert(
    mergedAcotarView.Count == 1,
    "Two messy real-world spellings of the same ACOTAR part did not collapse into one "
        + "Explore entry once their titles were canonicalized.");
Assert(
    mergedAcotarView[0].Title == "A Court of Thorns and Roses (Part 2 of 2) (Dramatized Adaptation)",
    "The surviving merged ACOTAR entry did not carry the canonical title.");

// The real drift measured off Explore's own catalogue: two copies of one recording, same
// canonical title after formatting, whose reported runtimes still differ by a fraction of a
// percent because one is a re-encode of the other. These used to survive as separate rows
// even after their titles converged, because the merge tolerance was a flat two seconds.
var fourthWingCopy1 = new BookFingerprint(
    1, new string('k', 64), 449_954_471, 28_800, "audio",
    "Fourth Wing (Part 1 of 2) (Dramatized Adaptation)", "Rebecca Yarros",
    "The Empyrean", 1, "Dramatized Adaptation", 1, 2);
var fourthWingCopy2 = new BookFingerprint(
    1, new string('l', 64), 450_760_050, 28_350.682, "m4a",
    "Fourth Wing (Part 1 of 2) (Dramatized Adaptation)", "Rebecca Yarros",
    "Fourth Wing", null, "Dramatized Adaptation", 1, 2);
// This real pair drifts 1.56%, past the 0.2% re-encoding tolerance, so it is deliberately
// left unmerged here for the evidence-based admin duplicate tool (chapter structure, not a
// runtime guess) to resolve with actual proof rather than being folded in on a coincidence.
Assert(
    ExploreCatalog.Deduplicate([
        ExploreCatalog.Create(fourthWingCopy1, messyAcotarResult),
        ExploreCatalog.Create(fourthWingCopy2, messyAcotarResult),
    ]).Count == 2,
    "Two Fourth Wing copies 1.56% apart merged on runtime alone; that drift is too large to "
        + "be re-encoding noise and this pair should be left for evidence-based matching.");

var funnyStoryCopy1 = new BookFingerprint(
    1, new string('m', 64), 1_000, 40_982.854, "audio/x-m4a",
    "Funny Story", "Emily Henry", null, null, null, null, null);
var funnyStoryCopy2 = new BookFingerprint(
    1, new string('n', 64), 1_000, 40_995.7, "application/octet-stream",
    "Funny Story", "Emily Henry", null, null, null, null, null);
Assert(
    ExploreCatalog.Deduplicate([
        ExploreCatalog.Create(funnyStoryCopy1, messyAcotarResult),
        ExploreCatalog.Create(funnyStoryCopy2, messyAcotarResult),
    ]).Count == 1,
    "Two real-world copies of Funny Story, 12.8 seconds apart on an 11-hour runtime, did "
        + "not merge.");

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
// Whisper's word timings are relative to the chunk it transcribed, exactly like the segment
// they sit inside. Only the segment bounds used to be shifted to absolute audiobook time,
// which left every word in a chunk after the first pointing at the wrong place in the audio
// once flattened out of its segment by TranscriptWordLocator, DeterministicContentDetector,
// or ReaderAlignment.
Assert(
    transcriptStore.Transcript?.Segments.Single().Words?.Single().StartTime == 10,
    "Chunk timestamp offset was not applied to word-level timings.");
Assert(
    transcriptStore.SaveCount == 2 && transcriptStore.Transcript?.IsComplete == true,
    "Partial and completed transcript checkpoints were not saved.");
Assert(
    pipelineResult.ScannerVersion == "contract-test",
    "Pipeline scanner version was not returned.");

// The materialized path (what ScanWorker actually runs against real uploads) offsets each
// chunk's segments to absolute audiobook time separately from the streaming path above.
// This must apply to word-level timings too, or every word past the first chunk resolves to
// the wrong position once flattened out of its segment by TranscriptWordLocator,
// DeterministicContentDetector, or ReaderAlignment.
var materializedTranscriptStore = new CapturingTranscriptStore();
var materializedPipeline = new ScanPipeline(
    new TwoChunkMaterializedAudioChunker(),
    new ChunkRelativeTranscriptionProvider(),
    new FakeAnalysisProvider(),
    materializedTranscriptStore,
    new OpenAIProcessingOptions { TranscriptionWorkers = 1, TranscriptionConcurrencyPerWorker = 1 },
    new ConcurrentChunkTranscriber(
        new ChunkRelativeTranscriptionProvider(),
        new OpenAIProcessingOptions { TranscriptionWorkers = 1, TranscriptionConcurrencyPerWorker = 1 },
        NullLogger<ConcurrentChunkTranscriber>.Instance));

await materializedPipeline.Process(
    upload with { IsUploaded = true, StoredPath = "/private/materialized-test.audio" },
    null,
    CancellationToken.None);

var materializedSegments = materializedTranscriptStore.Transcript!.Segments
    .OrderBy(segment => segment.StartTime)
    .ToArray();
Assert(
    materializedSegments.Length == 2,
    "Materialized pipeline did not produce one segment per chunk.");
Assert(
    materializedSegments[0].StartTime == 1 && materializedSegments[0].Words?.Single().StartTime == 1,
    "First chunk's segment/word timing should already be near-absolute with no meaningful offset.");
Assert(
    materializedSegments[1].StartTime == 601,
    "Materialized pipeline did not offset the second chunk's segment to absolute time.");
Assert(
    materializedSegments[1].Words?.Single().StartTime == 601 &&
    materializedSegments[1].Words?.Single().EndTime == 602,
    "Materialized pipeline did not offset the second chunk's word timings to absolute time; " +
    "they were left relative to the chunk, which is where they resolve to the wrong audio position.");

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
// No flat padding either side any more: with no word timing supplied here (a transcript
// saved before word timings existed), the merge falls back to the cluster's own raw range
// rather than inventing a pad. The word-snapped case, where a real boundary moves to the
// nearest actual word instead of a flat number of seconds, is exercised separately below.
Assert(joinedSceneEvents[0].StartTime == 100 && joinedSceneEvents[0].EndTime == 260,
    "A joined sexual scene with no transcript word timing available did not fall back to " +
    "its own unpadded raw range.");
Assert(joinedSceneEvents[0].SafeDescription == "Sustained sexual activity",
    "Sexual scene safe description fallback was not applied.");

// The word-snapped case: a merged scene's boundary moves to the transcript's own nearest
// word rather than a flat number of seconds, and does so independently on each side.
var snapWordedSegment = new TranscriptSegment(0, 600, "words around the scene", new[]
{
    new TranscriptWord("Before", 96.0, 96.8),
    new TranscriptWord("the", 96.8, 97.0),
    new TranscriptWord("scene", 97.0, 99.4), // spans the raw start of 100
    new TranscriptWord("begins.", 99.4, 100.6),
    new TranscriptWord("Afterward", 259.0, 261.5), // spans the raw end of 260
    new TranscriptWord("she", 261.5, 262.0),
    new TranscriptWord("left.", 262.0, 262.6),
});
var snappedSceneEvents = SceneEventPostProcessor.Process(
    [
        new ScanEvent(Guid.NewGuid(), 100, 180, completeSceneMapping.CategoryID,
            completeSceneMapping.GroupID, completeSceneMapping.EventID, .91, "snap-scene-a"),
        new ScanEvent(Guid.NewGuid(), 165, 260, completeSceneMapping.CategoryID,
            completeSceneMapping.GroupID, completeSceneMapping.EventID, .95, "snap-scene-b")
    ],
    [snapWordedSegment]);
Assert(snappedSceneEvents.Count == 1, "Overlapping sexual scene ranges were not joined.");
// Nearest word start to the raw 100 is "begins."'s own start (99.4); nearest word end to
// the raw 260 is "Afterward"'s own end (261.5).
Assert(
    snappedSceneEvents[0].StartTime == 99.4 && snappedSceneEvents[0].EndTime == 261.5,
    $"A merged scene's boundary did not snap to the transcript's own nearest word on each " +
    $"side (got {snappedSceneEvents[0].StartTime}-{snappedSceneEvents[0].EndTime}).");

// Sentence-boundary merge: replaces a flat 45-second gap. Two candidates close in time but
// separated by a complete, ordinary sentence must NOT merge -- this is the exact fault a
// tester reported, two brief encounters swallowed into one long skip because they happened
// to fall within a flat time window of each other.
var cleanSentenceBetween = SceneEventPostProcessor.Process(
    [
        new ScanEvent(Guid.NewGuid(), 80, 110, completeSceneMapping.CategoryID,
            completeSceneMapping.GroupID, completeSceneMapping.EventID, .91, "sentence-scene-a",
            "Sustained intimate encounter"),
        new ScanEvent(Guid.NewGuid(), 120, 140, completeSceneMapping.CategoryID,
            completeSceneMapping.GroupID, completeSceneMapping.EventID, .95, "sentence-scene-b",
            "Sustained intimate encounter")
    ],
    [new TranscriptSegment(0, 200, "surrounding narration"),
        new TranscriptSegment(110, 120, "He left the room. Morning came quickly.")]);
Assert(cleanSentenceBetween.Count == 2,
    "Two scene candidates separated by a complete, unrelated sentence were merged into one " +
    "skip despite being only ten seconds apart -- a flat time gap is driving the result " +
    "instead of the transcript's own content.");

// The same two candidates with only a trailing fragment between them -- no terminator at
// all -- must still merge, because nothing in the gap is a complete sentence of its own.
var fragmentBetween = SceneEventPostProcessor.Process(
    [
        new ScanEvent(Guid.NewGuid(), 80, 110, completeSceneMapping.CategoryID,
            completeSceneMapping.GroupID, completeSceneMapping.EventID, .91, "fragment-scene-a",
            "Sustained intimate encounter"),
        new ScanEvent(Guid.NewGuid(), 120, 140, completeSceneMapping.CategoryID,
            completeSceneMapping.GroupID, completeSceneMapping.EventID, .95, "fragment-scene-b",
            "Sustained intimate encounter")
    ],
    [new TranscriptSegment(0, 200, "surrounding narration"),
        new TranscriptSegment(110, 120, "and then, breathless--")]);
Assert(fragmentBetween.Count == 1,
    "Two scene candidates separated only by a trailing fragment with no sentence end were " +
    "not merged, even though nothing complete separates them.");

// Two candidates with no gap at all (back to back or overlapping) merge regardless -- there
// is no transcript text between them to contain a sentence in the first place.
var adjacentCandidates = SceneEventPostProcessor.Process(
    [
        new ScanEvent(Guid.NewGuid(), 100, 120, completeSceneMapping.CategoryID,
            completeSceneMapping.GroupID, completeSceneMapping.EventID, .91, "adjacent-scene-a",
            "Brief intimate encounter"),
        new ScanEvent(Guid.NewGuid(), 120, 140, completeSceneMapping.CategoryID,
            completeSceneMapping.GroupID, completeSceneMapping.EventID, .95, "adjacent-scene-b",
            "Sustained intimate encounter")
    ],
    [new TranscriptSegment(0, 200, "continuous narration throughout")]);
Assert(adjacentCandidates.Count == 1,
    "Two immediately adjacent scene candidates with no transcript text between them were " +
    "not merged.");

// A sentence ending in an abbreviation or initial ("Mr. Adams") must not be mistaken for a
// sentence break, or ordinary prose would fragment scenes that should stay merged.
var abbreviationBetween = SceneEventPostProcessor.Process(
    [
        new ScanEvent(Guid.NewGuid(), 80, 110, completeSceneMapping.CategoryID,
            completeSceneMapping.GroupID, completeSceneMapping.EventID, .91, "abbrev-scene-a",
            "Sustained intimate encounter"),
        new ScanEvent(Guid.NewGuid(), 120, 140, completeSceneMapping.CategoryID,
            completeSceneMapping.GroupID, completeSceneMapping.EventID, .95, "abbrev-scene-b",
            "Sustained intimate encounter")
    ],
    [new TranscriptSegment(0, 200, "surrounding narration"),
        new TranscriptSegment(110, 120, "Mr. Adams knocked")]);
Assert(abbreviationBetween.Count == 1,
    "An abbreviation's full stop between two scene candidates was mistaken for a genuine " +
    "sentence end, splitting a merge that should have held.");

// Terra's model-input context window: sized by sentence structure, never a flat number of
// seconds, and this window must never become a stored boundary -- only used to decide what
// text the model reads.
{
    var contextSegments = new[]
    {
        new TranscriptSegment(0, 10, "Long before this, they had argued in the kitchen."),
        new TranscriptSegment(10, 20, "That was over now."),
        new TranscriptSegment(20, 30, "She crossed the room toward him"), // no terminator: mid-thought
        new TranscriptSegment(30, 40, "and kissed him without a word."),
        new TranscriptSegment(40, 50, "Later they lay together, breathing slowly."),
        new TranscriptSegment(50, 60, "Morning came, and with it the ordinary day."),
        new TranscriptSegment(60, 70, "He made coffee while she dressed."),
    };

    // The candidate spans 25-45, inside the "she crossed the room... breathing slowly"
    // passage. Backward, the window must stop expanding once it reaches a segment that
    // itself closes a sentence -- here, "That was over now." (10-20) -- rather than reading
    // in the unrelated argument before it. Forward, it must stop at "Morning came..."
    // (50-60), which itself closes a sentence, rather than continuing into the coffee line.
    var context = OpenAIContentAnalysisProvider.SentenceBoundedContext(25, 45, contextSegments);
    Assert(
        context.Count > 0 && context[0].StartTime == 10,
        $"Terra's context window did not stop expanding backward at the nearest sentence " +
        $"boundary (started at {context.FirstOrDefault()?.StartTime}).");
    Assert(
        context[^1].EndTime == 60,
        $"Terra's context window did not stop expanding forward at the nearest sentence " +
        $"boundary (ended at {context[^1].EndTime}).");

    // A candidate whose surrounding text has no sentence terminator at all within the cap
    // must still stop at the cap rather than reading indefinitely.
    var unpunctuated = Enumerable.Range(0, 20)
        .Select(i => new TranscriptSegment(i * 10, i * 10 + 10, $"word{i} continues without end"))
        .ToArray();
    var cappedContext = OpenAIContentAnalysisProvider.SentenceBoundedContext(95, 105, unpunctuated);
    Assert(
        cappedContext[0].StartTime >= 95 - 60 && cappedContext[^1].EndTime <= 105 + 60,
        "An unpunctuated passage's context window expanded past its capped ceiling.");
    Assert(
        cappedContext[0].StartTime > 95 - 60 - 10 && cappedContext[^1].EndTime < 105 + 60 + 10,
        "An unpunctuated passage's context window did not reach its capped ceiling at all.");
}

// Sol's own boundary rule: a quote-confirmed start, a claimed end no earlier than that
// start, then up to one second of allowance on each side snapped to the nearest actual word.
{
    var solWords = new TranscriptSegment(
        500, 512, "She paused, then leaned in and kissed him slowly by the fire.", new[]
        {
            new TranscriptWord("She", 500.0, 500.3),
            new TranscriptWord("paused,", 500.3, 500.8),
            new TranscriptWord("then", 500.8, 501.0),
            new TranscriptWord("leaned", 501.0, 501.4),
            new TranscriptWord("in", 501.4, 501.6),
            new TranscriptWord("and", 501.6, 501.8),
            new TranscriptWord("kissed", 501.8, 502.3),
            new TranscriptWord("him", 502.3, 502.5),
            new TranscriptWord("slowly", 502.5, 503.0),
            new TranscriptWord("by", 503.0, 503.2),
            new TranscriptWord("the", 503.2, 503.4),
            new TranscriptWord("fire.", 503.4, 503.9),
        });

    // A slightly imprecise proposed timestamp still resolves to the exact confirmed word
    // boundary, expanded by at most one second and snapped to the nearest real word.
    var resolved = OpenAIContentAnalysisProvider.ResolveConfirmedSceneBoundary(
        501.0, 502.6, [solWords]);
    Assert(
        resolved.Start == 500.8, // one second back from 501.0 reaches "then" (500.8-501.0)
        $"Sol's confirmed start did not expand by up to one second and snap to the nearest " +
        $"word (got {resolved.Start}).");
    Assert(
        resolved.End == 503.0, // one second forward from 502.6 reaches "slowly"'s own end (503.0)
        $"Sol's confirmed end did not expand by up to one second and snap to the nearest " +
        $"word (got {resolved.End}).");

    // A claimed end earlier than the confirmed start must never produce an inverted range.
    var invertedGuard = OpenAIContentAnalysisProvider.ResolveConfirmedSceneBoundary(
        502.3, 500.0, [solWords]);
    Assert(
        invertedGuard.End >= invertedGuard.Start,
        "A claimed end earlier than the confirmed start produced an inverted final range.");

    // The full decision, including rejection: a quote that actually exists in the
    // transcript confirms and finalizes; an empty quote, or one that does not appear
    // anywhere in the supplied transcript, is rejected outright rather than falling back
    // to some other range -- a finalized boundary this pipeline cannot confirm must not
    // reach a listener presented as confirmed.
    var confirmedByQuote = OpenAIContentAnalysisProvider.TryConfirmSceneBoundary(
        "leaned in and kissed him", 502.6, [solWords]);
    Assert(
        confirmedByQuote is not null,
        "A quote that genuinely exists in the transcript was not confirmed.");

    var rejectedForNoQuote = OpenAIContentAnalysisProvider.TryConfirmSceneBoundary(
        "", 502.6, [solWords]);
    Assert(
        rejectedForNoQuote is null,
        "An escalation with no supporting quote at all was confirmed instead of rejected.");

    var rejectedForFabricatedQuote = OpenAIContentAnalysisProvider.TryConfirmSceneBoundary(
        "words that never appear in this transcript at all", 502.6, [solWords]);
    Assert(
        rejectedForFabricatedQuote is null,
        "A quote absent from the transcript entirely was confirmed instead of rejected -- " +
        "this is exactly the fabricated-evidence case the rejection exists to catch.");
}

// Luna batching: reduced overlap and paragraph/sentence-aligned boundaries instead of a
// fixed segment count.
{
    // A transcript with a clear sentence break near the fixed target boundary: the batch
    // should land on that break rather than the arbitrary fixed count.
    var sentenceAlignedSegments = Enumerable.Range(0, 20)
        .Select(i => new TranscriptSegment(
            i * 10, i * 10 + 10,
            i == 7 ? "And that was the end of it." : $"Segment {i} continues the story"))
        .ToArray();
    var alignedRanges = OpenAIContentAnalysisProvider.ComputeAnalysisBatchRanges(
        sentenceAlignedSegments, batchSize: 10);
    Assert(
        alignedRanges.Count > 0 && alignedRanges[0].EndExclusive == 8,
        $"The first batch did not land on the nearby sentence boundary at segment index 7 " +
        $"(got EndExclusive={alignedRanges.FirstOrDefault().EndExclusive}).");

    // Total coverage: every segment must still be covered by at least one batch, even with
    // natural-break adjustment.
    var maxCovered = alignedRanges.Max(range => range.EndExclusive);
    Assert(
        maxCovered == sentenceAlignedSegments.Length,
        $"Paragraph-aligned batching left segments uncovered (covered up to {maxCovered} of " +
        $"{sentenceAlignedSegments.Length}).");

    // Reduced overlap: two consecutive batches over a long, unpunctuated transcript (so no
    // natural break shifts the boundary) share close to 15% of a batch, not 50%.
    var unpunctuatedSegments = Enumerable.Range(0, 40)
        .Select(i => new TranscriptSegment(i * 10, i * 10 + 10, $"word{i} continues without end"))
        .ToArray();
    var unpunctuatedRanges = OpenAIContentAnalysisProvider.ComputeAnalysisBatchRanges(
        unpunctuatedSegments, batchSize: 10);
    Assert(unpunctuatedRanges.Count >= 2, "Expected at least two batches over 40 segments.");
    var firstBatchOverlap = unpunctuatedRanges[0].EndExclusive - unpunctuatedRanges[1].StartIndex;
    Assert(
        firstBatchOverlap is >= 0 and <= 3,
        $"Batch overlap was {firstBatchOverlap} segments against a batch size of 10, which is " +
        "no longer close to the intended 15% (roughly 1-2 segments), not the old 50%.");

    // Empty transcript produces no batches rather than throwing.
    Assert(
        OpenAIContentAnalysisProvider.ComputeAnalysisBatchRanges([], batchSize: 10).Count == 0,
        "An empty transcript produced a batch range instead of none.");
}

// Terra entry gate: a lone weak sexual-content mention never reaches Terra at all, but a
// dense cluster of them, or one alongside a stronger label, still does.
{
    var weakDialogueMapping = ContentTaxonomy.Mappings["sexual_suggestive_dialogue"];
    var weakReferenceMapping = ContentTaxonomy.Mappings["sexual_references"];
    var explicitMapping = ContentTaxonomy.Mappings["sexual_explicit_activity"];
    var wideNarrativeSegments = new[] { new TranscriptSegment(0, 1000, "surrounding narration") };

    // A lone isolated flirtatious line, nothing else nearby: excluded.
    var loneWeakMention = new[]
    {
        new ScanEvent(Guid.NewGuid(), 100, 105, weakDialogueMapping.CategoryID,
            weakDialogueMapping.GroupID, weakDialogueMapping.EventID, .7, "lone-weak"),
    };
    Assert(
        OpenAIContentAnalysisProvider.ExcludeLoneWeakSingletons(
            loneWeakMention, wideNarrativeSegments).Count == 0,
        "An isolated flirtatious line with nothing else nearby was still sent toward Terra.");

    // Three weak mentions close together with nothing but continuous narration between
    // them form a dense cluster and are kept.
    var denseCluster = new[]
    {
        new ScanEvent(Guid.NewGuid(), 100, 105, weakDialogueMapping.CategoryID,
            weakDialogueMapping.GroupID, weakDialogueMapping.EventID, .7, "cluster-1"),
        new ScanEvent(Guid.NewGuid(), 106, 110, weakReferenceMapping.CategoryID,
            weakReferenceMapping.GroupID, weakReferenceMapping.EventID, .7, "cluster-2"),
        new ScanEvent(Guid.NewGuid(), 111, 115, weakDialogueMapping.CategoryID,
            weakDialogueMapping.GroupID, weakDialogueMapping.EventID, .7, "cluster-3"),
    };
    Assert(
        OpenAIContentAnalysisProvider.ExcludeLoneWeakSingletons(
            denseCluster, wideNarrativeSegments).Count == 3,
        "A dense cluster of three weak mentions with nothing separating them was excluded " +
        "from reaching Terra.");

    // Two weak mentions separated by a complete sentence of ordinary narrative do not form
    // a cluster and are excluded individually.
    var brokenCluster = new[]
    {
        new ScanEvent(Guid.NewGuid(), 100, 105, weakDialogueMapping.CategoryID,
            weakDialogueMapping.GroupID, weakDialogueMapping.EventID, .7, "broken-1"),
        new ScanEvent(Guid.NewGuid(), 200, 205, weakReferenceMapping.CategoryID,
            weakReferenceMapping.GroupID, weakReferenceMapping.EventID, .7, "broken-2"),
    };
    var brokenSegments = new[]
    {
        new TranscriptSegment(0, 1000, "surrounding narration"),
        new TranscriptSegment(105, 200, "He left the room. Morning came quickly."),
    };
    Assert(
        OpenAIContentAnalysisProvider.ExcludeLoneWeakSingletons(brokenCluster, brokenSegments)
            .Count == 0,
        "Two weak mentions separated by a complete sentence of ordinary narrative were " +
        "treated as one dense cluster.");

    // A lone weak mention sharing an unbroken passage with a stronger label (explicit
    // activity) is kept as context for that activity rather than excluded as noise.
    var weakBesideStrong = new[]
    {
        new ScanEvent(Guid.NewGuid(), 100, 105, weakDialogueMapping.CategoryID,
            weakDialogueMapping.GroupID, weakDialogueMapping.EventID, .7, "beside-strong-weak"),
        new ScanEvent(Guid.NewGuid(), 106, 120, explicitMapping.CategoryID,
            explicitMapping.GroupID, explicitMapping.EventID, .9, "beside-strong-explicit"),
    };
    var keptBesideStrong = OpenAIContentAnalysisProvider.ExcludeLoneWeakSingletons(
        weakBesideStrong, wideNarrativeSegments);
    Assert(
        keptBesideStrong.Any(item => item.StableKey == "beside-strong-weak"),
        "A weak mention sharing an unbroken passage with a stronger label was excluded " +
        "instead of kept as context for that activity.");
}

// Sol dispatch gating: only what is genuinely ambiguous or borderline reaches Sol, so the
// pipeline stops re-paying for every Terra accept regardless of confidence.
{
    const double threshold = .95;
    OpenAIContentAnalysisProvider.VerifiedSceneCandidate Decision(
        bool accepted, bool needsEscalation, double confidence) => new(
        "candidate", accepted, needsEscalation, true, true, 0, 10, confidence, "description");

    // Terra's own needsEscalation flag always sends it to Sol, regardless of confidence.
    Assert(
        OpenAIContentAnalysisProvider.NeedsSolReview(Decision(false, true, .99), threshold),
        "A candidate Terra flagged needsEscalation was not sent to Sol.");

    // A confident accept at or above the threshold skips Sol.
    Assert(
        !OpenAIContentAnalysisProvider.NeedsSolReview(Decision(true, false, .97), threshold),
        "A confident Terra accept above the threshold was still sent to Sol.");

    // An accept just below the threshold still needs Sol's review.
    Assert(
        OpenAIContentAnalysisProvider.NeedsSolReview(Decision(true, false, .90), threshold),
        "A Terra accept below the confidence threshold was not sent to Sol.");

    // A rejected candidate (neither accepted nor flagged) needs nothing further.
    Assert(
        !OpenAIContentAnalysisProvider.NeedsSolReview(Decision(false, false, .99), threshold),
        "A candidate Terra rejected outright was sent to Sol anyway.");
}

// Sexual-violence lane: a separate finalized label from the consensual scene lane, requiring
// its own consent-related evidence, and mutually exclusive with it.
{
    OpenAIContentAnalysisProvider.VerifiedSceneCandidate Decision(
        bool accepted, bool directEvidence, bool sustained, bool nonconsensual,
        double confidence = .95) => new(
        "candidate", accepted, false, directEvidence, sustained, 0, 10, confidence,
        "description", Quote: "quote", NonconsensualEvidence: nonconsensual);

    // The consensual lane never looks at nonconsensualEvidence at all -- an accepted,
    // sustained candidate finalizes as sexual_complete_scene regardless of that field.
    var consensualOutcome = OpenAIContentAnalysisProvider.ResolveSceneOutcome(
        "sexual_complete_scene", Decision(true, true, true, nonconsensual: true));
    Assert(
        consensualOutcome is { Label: "sexual_complete_scene" },
        "A candidate reviewed under the consensual lane did not finalize as " +
        "sexual_complete_scene regardless of an incidentally-true nonconsensualEvidence field.");

    // The sexual_violence lane requires nonconsensualEvidence specifically; without it, an
    // otherwise-accepted candidate must not finalize as sexual_violence.
    var missingConsentEvidence = OpenAIContentAnalysisProvider.ResolveSceneOutcome(
        "sexual_violence", Decision(true, true, true, nonconsensual: false));
    Assert(
        missingConsentEvidence is null,
        "A sexual_violence candidate with directSexualActEvidence and sustainedBeyondKissing " +
        "true, but nonconsensualEvidence false, still finalized as an event -- ambiguous or " +
        "absent consent evidence must not become a reported assault.");

    // With all three evidence fields true and confidence clearing the floor, the
    // sexual_violence lane finalizes as its own label, never sexual_complete_scene.
    var violenceOutcome = OpenAIContentAnalysisProvider.ResolveSceneOutcome(
        "sexual_violence", Decision(true, true, true, nonconsensual: true));
    Assert(
        violenceOutcome is { Label: "sexual_violence" } outcome &&
            outcome.Mapping.EventID == ContentTaxonomy.Mappings["sexual_violence"].EventID,
        "A fully-confirmed sexual_violence candidate did not finalize under its own taxonomy " +
        "mapping.");
    Assert(
        violenceOutcome!.Value.Mapping.EventID !=
            ContentTaxonomy.Mappings["sexual_complete_scene"].EventID,
        "A confirmed sexual_violence candidate finalized under the consensual scene's mapping.");

    // Confidence below the 0.85 scene floor rejects outright, same as the consensual lane.
    var lowConfidence = OpenAIContentAnalysisProvider.ResolveSceneOutcome(
        "sexual_violence", Decision(true, true, true, nonconsensual: true, confidence: .70));
    Assert(
        lowConfidence is null,
        "A sexual_violence candidate below the confidence floor still finalized as an event.");
}

// Lane separation in coalescing: a sexual_violence candidate and a nearby consensual-scene
// candidate must never be merged into one Terra review window, even when close in time.
{
    var violenceCandidateSegment = new TranscriptSegment(0, 5, "he refused to stop despite her protest");
    var consensualCandidateSegment = new TranscriptSegment(10, 15, "they moved together willingly");

    var mixedLaneCandidates = new[]
    {
        new OpenAIContentAnalysisProvider.SceneVerificationCandidate(
            "violence-1", 0, 5, [violenceCandidateSegment], "sexual_violence"),
        new OpenAIContentAnalysisProvider.SceneVerificationCandidate(
            "consensual-1", 10, 15, [consensualCandidateSegment], "sexual_complete_scene"),
    };
    var coalesced = OpenAIContentAnalysisProvider.CoalesceSceneCandidates(mixedLaneCandidates);
    Assert(
        coalesced.Count == 2,
        $"A sexual_violence candidate and a nearby consensual-scene candidate, only ten " +
        $"seconds apart, were merged into one Terra review window (got {coalesced.Count} " +
        "windows instead of 2) -- Luna already treats the two as mutually exclusive claims " +
        "and they must not be reviewed together.");
    Assert(
        coalesced.All(item => item.FirstPassLane is "sexual_violence" or "sexual_complete_scene") &&
            coalesced.Select(item => item.FirstPassLane).Distinct().Count() == 2,
        "Coalescing did not preserve each candidate's own first-pass lane.");
}

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

// sexual_violence gets the same merge/word-snap/minimum-length treatment as
// sexual_complete_scene, but clustered entirely separately -- a confirmed assault scene and
// a confirmed consensual scene must never merge into one skip even when adjacent in time.
{
    var sexualViolenceMapping = ContentTaxonomy.Mappings["sexual_violence"];

    // A standalone sexual_violence scene clears the same 15-second floor and gets the same
    // structural treatment (merge/floor) as a consensual scene.
    var violenceOnly = SceneEventPostProcessor.Process(
        [
            new ScanEvent(Guid.NewGuid(), 300, 380, sexualViolenceMapping.CategoryID,
                sexualViolenceMapping.GroupID, sexualViolenceMapping.EventID, .94, "violence-scene",
                "Sexual violence is described")
        ],
        [new TranscriptSegment(0, 600, "Test transcript")]);
    Assert(
        violenceOnly.Count == 1 && violenceOnly[0].EventID == sexualViolenceMapping.EventID,
        "A standalone sexual_violence event above the minimum length did not survive " +
        "post-processing under its own event ID.");

    // A confirmed sexual_violence scene and a confirmed consensual scene, immediately
    // adjacent in time with no transcript text between them (the case that would merge two
    // same-label candidates), must remain two separate events, never one merged skip.
    var mixedLabels = SceneEventPostProcessor.Process(
        [
            new ScanEvent(Guid.NewGuid(), 100, 140, sexualViolenceMapping.CategoryID,
                sexualViolenceMapping.GroupID, sexualViolenceMapping.EventID, .94, "adjacent-violence",
                "Sexual violence is described"),
            new ScanEvent(Guid.NewGuid(), 140, 180, completeSceneMapping.CategoryID,
                completeSceneMapping.GroupID, completeSceneMapping.EventID, .94, "adjacent-consensual",
                "Sustained intimate encounter"),
        ],
        [new TranscriptSegment(0, 600, "continuous narration throughout")]);
    Assert(
        mixedLabels.Count == 2 &&
            mixedLabels.Any(item => item.EventID == sexualViolenceMapping.EventID) &&
            mixedLabels.Any(item => item.EventID == completeSceneMapping.EventID),
        $"An adjacent sexual_violence event and sexual_complete_scene event were merged " +
        $"into one skip across label boundaries (got {mixedLabels.Count} event(s)) -- the " +
        "two labels must never merge into each other regardless of time proximity.");
}

// End-to-end pipeline: a fixture transcript run through the full updated scanner --
// deterministic profanity, Luna, narrow-violence policy, the Terra entry gate, Terra, the
// Sol dispatch gate, Sol, and sentence-boundary scene merging -- asserting the final result
// is word-snapped and routed the way the overhaul intends.
{
    var checkpointRoot = Path.Combine(
        Path.GetTempPath(), $"audiochoice-e2e-checkpoints-{Guid.NewGuid():N}");
    try
    {
        var e2eOptions = new OpenAIProcessingOptions
        {
            AnalysisModel = "gpt-5.6-luna",
            SceneVerificationModel = "gpt-5.6-terra",
            SceneEscalationModel = "gpt-5.6-sol",
            ViolenceVerificationModel = "gpt-5.6-terra",
            SolEscalationConfidenceThreshold = .95,
            MinimumEventConfidence = .55,
        };

        // A transcript with: one profane word (deterministic, never touches Luna), one
        // ordinary conflict sentence (must not become graphic violence), and one sexual
        // scene spanning two Luna batches, confirmed at high confidence by Terra without
        // needing Sol at all.
        var e2eSegments = new[]
        {
            new TranscriptSegment(0, 5, "Damn it, he muttered, and slammed the car door.", new[]
            {
                new TranscriptWord("Damn", 0.2, 0.6),
                new TranscriptWord("it,", 0.6, 0.8),
            }),
            new TranscriptSegment(5, 10, "They argued for a while about the schedule."),
            new TranscriptSegment(
                10, 20, "She crossed the room and kissed him slowly by the fire.", new[]
                {
                    new TranscriptWord("She", 10.0, 10.3),
                    new TranscriptWord("crossed", 10.3, 10.7),
                    new TranscriptWord("the", 10.7, 10.9),
                    new TranscriptWord("room", 10.9, 11.3),
                    new TranscriptWord("and", 11.3, 11.5),
                    new TranscriptWord("kissed", 11.5, 12.0),
                    new TranscriptWord("him", 12.0, 12.2),
                    new TranscriptWord("slowly", 12.2, 12.7),
                    new TranscriptWord("by", 12.7, 12.9),
                    new TranscriptWord("the", 12.9, 13.1),
                    new TranscriptWord("fire.", 13.1, 13.6),
                }),
            new TranscriptSegment(
                20, 30, "Clothes fell away as they moved together on the rug for a while.", new[]
                {
                    new TranscriptWord("Clothes", 20.0, 20.5),
                    new TranscriptWord("fell", 20.5, 20.8),
                    new TranscriptWord("away", 20.8, 21.2),
                    new TranscriptWord("as", 21.2, 21.4),
                    new TranscriptWord("they", 21.4, 21.6),
                    new TranscriptWord("moved", 21.6, 22.0),
                    new TranscriptWord("together", 22.0, 22.6),
                    new TranscriptWord("on", 22.6, 22.8),
                    new TranscriptWord("the", 22.8, 23.0),
                    new TranscriptWord("rug", 23.0, 23.5),
                    new TranscriptWord("for", 23.5, 23.7),
                    new TranscriptWord("a", 23.7, 23.8),
                    new TranscriptWord("while.", 25.8, 26.3),
                }),
            new TranscriptSegment(30, 40, "Morning came, and with it the ordinary day."),
        };

        var e2eModelClient = new FixtureAnalysisModelClient();
        var e2eDataPaths = new AudioChoiceDataPaths(
            new FakeWebHostEnvironment(checkpointRoot),
            new ConfigurationBuilder().Build());
        var e2eProvider = new OpenAIContentAnalysisProvider(
            e2eModelClient, e2eOptions, e2eDataPaths,
            NullLogger<OpenAIContentAnalysisProvider>.Instance);

        var e2eResult = await e2eProvider.Analyze(e2eSegments, null, CancellationToken.None);

        var profanityEvent = e2eResult.SingleOrDefault(
            item => item.EventID == ContentTaxonomy.Mappings["profanity_mild"].EventID);
        Assert(
            profanityEvent is not null && Math.Abs(profanityEvent.StartTime - 0.2) < 0.001 &&
                Math.Abs(profanityEvent.EndTime - 0.6) < 0.001,
            "The end-to-end pipeline did not detect the deterministic profanity word at its " +
            "own word-level timing.");

        Assert(
            !e2eResult.Any(item => item.EventID == ContentTaxonomy.Mappings["violence_graphic"].EventID),
            "The end-to-end pipeline reported graphic violence for an ordinary argument.");

        var sceneEvent = e2eResult.SingleOrDefault(
            item => item.EventID == ContentTaxonomy.Mappings["sexual_complete_scene"].EventID);
        Assert(
            sceneEvent is not null,
            "The end-to-end pipeline did not produce a complete-scene event for the fixture " +
            "sexual scene.");
        // Word-snapped, not flat-padded: the scene's boundary must land exactly on one of
        // the fixture's own word timings, not on some multi-second-padded number.
        var allWordTimes = e2eSegments
            .Where(segment => segment.Words is not null)
            .SelectMany(segment => segment.Words!)
            .SelectMany(word => new[] { word.StartTime, word.EndTime })
            .ToHashSet();
        Assert(
            allWordTimes.Contains(sceneEvent!.StartTime) && allWordTimes.Contains(sceneEvent.EndTime),
            $"The end-to-end scene's boundary ({sceneEvent.StartTime}-{sceneEvent.EndTime}) " +
            "did not land on any of the fixture transcript's own word timings.");

        Assert(
            e2eModelClient.SolCallCount == 0,
            "The end-to-end pipeline invoked Sol for a scene Terra confirmed at high " +
            "confidence, which the Sol dispatch gate exists to avoid.");
        Assert(
            e2eModelClient.LunaCallCount >= 1 && e2eModelClient.TerraCallCount >= 1,
            "The end-to-end pipeline did not exercise Luna and Terra at all.");
    }
    finally
    {
        if (Directory.Exists(checkpointRoot)) Directory.Delete(checkpointRoot, true);
    }
}

// End-to-end pipeline: sexual_violence. A fixture transcript whose only sexual-content
// candidate is non-consensual must finalize as sexual_violence -- never
// sexual_complete_scene -- and must go through the same word-snap and Sol-dispatch-gate
// treatment the consensual lane gets.
{
    var violenceCheckpointRoot = Path.Combine(
        Path.GetTempPath(), $"audiochoice-e2e-violence-checkpoints-{Guid.NewGuid():N}");
    try
    {
        var violenceOptions = new OpenAIProcessingOptions
        {
            AnalysisModel = "gpt-5.6-luna",
            SceneVerificationModel = "gpt-5.6-terra",
            SceneEscalationModel = "gpt-5.6-sol",
            ViolenceVerificationModel = "gpt-5.6-terra",
            SolEscalationConfidenceThreshold = .95,
            MinimumEventConfidence = .55,
        };

        var violenceSegments = new[]
        {
            new TranscriptSegment(0, 5, "Morning came quietly at first."),
            new TranscriptSegment(
                10, 20, "He refused to let go despite her clear protest and struggle.", new[]
                {
                    new TranscriptWord("He", 10.0, 10.2),
                    new TranscriptWord("refused", 10.2, 10.6),
                    new TranscriptWord("to", 10.6, 10.7),
                    new TranscriptWord("let", 10.7, 10.9),
                    new TranscriptWord("go", 10.9, 11.1),
                    new TranscriptWord("despite", 11.1, 11.5),
                    new TranscriptWord("her", 11.5, 11.7),
                    new TranscriptWord("clear", 11.7, 12.0),
                    new TranscriptWord("protest", 12.0, 12.5),
                    new TranscriptWord("and", 12.5, 12.7),
                    new TranscriptWord("struggle.", 12.7, 27.0),
                }),
            new TranscriptSegment(30, 40, "Afterward, the house fell silent."),
        };

        var violenceModelClient = new FixtureAnalysisModelClient();
        var violenceDataPaths = new AudioChoiceDataPaths(
            new FakeWebHostEnvironment(violenceCheckpointRoot),
            new ConfigurationBuilder().Build());
        var violenceProvider = new OpenAIContentAnalysisProvider(
            violenceModelClient, violenceOptions, violenceDataPaths,
            NullLogger<OpenAIContentAnalysisProvider>.Instance);

        var violenceResult = await violenceProvider.Analyze(
            violenceSegments, null, CancellationToken.None);

        var violenceEvent = violenceResult.SingleOrDefault(
            item => item.EventID == ContentTaxonomy.Mappings["sexual_violence"].EventID);
        Assert(
            violenceEvent is not null,
            "The end-to-end pipeline did not produce a sexual_violence event for the " +
            "fixture's non-consensual passage.");
        Assert(
            !violenceResult.Any(item => item.EventID == ContentTaxonomy.Mappings["sexual_complete_scene"].EventID),
            "The end-to-end pipeline reported the fixture's non-consensual passage as a " +
            "consensual sexual_complete_scene, which the two labels' mutual exclusivity " +
            "must prevent.");

        var violenceWordTimes = violenceSegments
            .Where(segment => segment.Words is not null)
            .SelectMany(segment => segment.Words!)
            .SelectMany(word => new[] { word.StartTime, word.EndTime })
            .ToHashSet();
        Assert(
            violenceWordTimes.Contains(violenceEvent!.StartTime) &&
                violenceWordTimes.Contains(violenceEvent.EndTime),
            $"The end-to-end sexual_violence event's boundary " +
            $"({violenceEvent.StartTime}-{violenceEvent.EndTime}) did not land on any of " +
            "the fixture transcript's own word timings.");

        Assert(
            violenceModelClient.SolCallCount == 0,
            "The end-to-end pipeline invoked Sol for a sexual_violence candidate Terra " +
            "confirmed at high confidence, which the Sol dispatch gate exists to avoid.");
        Assert(
            violenceModelClient.TerraCallCount >= 1,
            "The end-to-end pipeline did not send the sexual_violence candidate to Terra " +
            "at all.");
    }
    finally
    {
        if (Directory.Exists(violenceCheckpointRoot)) Directory.Delete(violenceCheckpointRoot, true);
    }
}

// Negative-control regression guard: a book with nothing objectionable in it must produce
// (near-)zero events after the full pipeline overhaul, so a precision regression (the
// pipeline over-firing on ordinary prose) is caught here rather than discovered by a
// listener hearing a false positive. Text drawn from real public-domain openings -- Pride
// and Prejudice, chapter one -- with no model calls needed at all: every detector this
// pipeline runs before Luna is deterministic (profanity, the plausibility guard), and this
// fixture never reaches Luna in the first place because there is nothing for a batch to
// even contain besides ordinary narration, which is exactly the property being asserted.
{
    var knownCleanRoot = Path.Combine(
        Path.GetTempPath(), $"audiochoice-negative-control-{Guid.NewGuid():N}");
    try
    {
        var cleanOptions = new OpenAIProcessingOptions
        {
            AnalysisModel = "gpt-5.6-luna",
            SceneVerificationModel = "gpt-5.6-terra",
            SceneEscalationModel = "gpt-5.6-sol",
        };
        var cleanModelClient = new FixtureAnalysisModelClient();
        var cleanDataPaths = new AudioChoiceDataPaths(
            new FakeWebHostEnvironment(knownCleanRoot),
            new ConfigurationBuilder().Build());
        var cleanProvider = new OpenAIContentAnalysisProvider(
            cleanModelClient, cleanOptions, cleanDataPaths,
            NullLogger<OpenAIContentAnalysisProvider>.Instance);

        var cleanText = new[]
        {
            "It is a truth universally acknowledged, that a single man in possession of a " +
                "good fortune, must be in want of a wife.",
            "However little known the feelings or views of such a man may be on his first " +
                "entering a neighbourhood, this truth is so well fixed in the minds of the " +
                "surrounding families, that he is considered as the rightful property of " +
                "some one or other of their daughters.",
            "\"My dear Mr. Bennet,\" said his lady to him one day, \"have you heard that " +
                "Netherfield Park is let at last?\"",
            "Mr. Bennet replied that he had not.",
            "\"But it is,\" returned she; \"for Mrs. Long has just been here, and she told " +
                "me all about it.\"",
            "Mr. Bennet made no answer.",
            "\"Do not you want to know who has taken it?\" cried his wife impatiently.",
            "\"You want to tell me, and I have no objection to hearing it.\"",
            "This was invitation enough.",
            "\"Why, my dear, you must know, Mrs. Long says that Netherfield is taken by a " +
                "young man of large fortune from the north of England.\"",
        };
        var cleanSegments = cleanText
            .Select((text, index) => new TranscriptSegment(index * 8.0, index * 8.0 + 7.5, text))
            .ToArray();

        var cleanResult = await cleanProvider.Analyze(cleanSegments, null, CancellationToken.None);

        Assert(
            cleanResult.Count == 0,
            $"A known-clean passage of ordinary narrative produced {cleanResult.Count} " +
            "event(s); the pipeline is over-firing on prose containing nothing objectionable.");
        Assert(
            cleanModelClient.TerraCallCount == 0 && cleanModelClient.SolCallCount == 0,
            "A known-clean passage with nothing for Luna to propose still reached Terra or " +
            "Sol, which should never be invoked when there is no candidate to review.");
    }
    finally
    {
        if (Directory.Exists(knownCleanRoot)) Directory.Delete(knownCleanRoot, true);
    }
}

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

// OpenAI's transcription API returns one flat words array for the whole chunk, unlike
// faster-whisper which nests each segment's own words inside it. Whoever consumes this
// provider's output (TranscriptWordLocator, DeterministicContentDetector, ReaderAlignment)
// only ever reads segment.Words, so a real regression here would look identical to the
// chunk-offset bug: word timings silently absent, with nothing to indicate why.
{
    var openAIResponseJson = """
        {
          "segments": [
            { "start": 0.0, "end": 2.0, "text": "Hello there" },
            { "start": 2.0, "end": 4.0, "text": "friend indeed" }
          ],
          "words": [
            { "word": "Hello", "start": 0.0, "end": 0.5 },
            { "word": " there", "start": 0.5, "end": 1.9 },
            { "word": " friend", "start": 2.1, "end": 3.0 },
            { "word": " indeed", "start": 3.0, "end": 3.9 }
          ]
        }
        """;
    using var fakeHandler = new FakeHttpMessageHandler(openAIResponseJson);
    using var openAIHttpClient = new HttpClient(fakeHandler) { BaseAddress = new Uri("https://fake.test/v1/") };
    var openAIProvider = new OpenAITranscriptionProvider(
        openAIHttpClient,
        new OpenAIProcessingOptions { ApiKey = "test-key", TranscriptionModel = "whisper-1" },
        NullLogger<OpenAITranscriptionProvider>.Instance);

    var tempChunkFile = Path.GetTempFileName();
    try
    {
        var openAISegments = await openAIProvider.Transcribe(
            new AudioChunk(tempChunkFile, 0, 4), CancellationToken.None);

        Assert(openAISegments.Count == 2, "OpenAI transcription provider dropped or split a segment.");
        Assert(
            openAISegments[0].Words?.Count == 2 && openAISegments[0].Words![0].Text == "Hello" &&
            openAISegments[0].Words![1].Text == "there",
            "OpenAI transcription provider did not attach the first segment's own words to it.");
        Assert(
            openAISegments[1].Words?.Count == 2 && openAISegments[1].Words![0].Text == "friend" &&
            openAISegments[1].Words![1].Text == "indeed",
            "OpenAI transcription provider did not attach the second segment's own words to it, " +
            "or assigned a word across the segment boundary to the wrong segment.");
        Assert(
            openAISegments[0].Words![0].StartTime == 0.0 && openAISegments[1].Words![1].EndTime == 3.9,
            "OpenAI transcription provider did not preserve each word's own start/end timing.");
    }
    finally
    {
        File.Delete(tempChunkFile);
    }
}

// Two audiobooks' chunks must actually transcribe at the same time once the semaphore
// permits it, not merely be scheduled without visible error. Proven by recording each
// call's own start/end instant against a wall clock and asserting two windows overlap --
// which also proves the test would catch the mismatch it exists for: run the identical
// scenario at a concurrency of one below, and it correctly reports no overlap at all.
var overlapProvider = new TimestampingFakeProvider(TimeSpan.FromMilliseconds(120));
var overlapScheduler = new ConcurrentChunkTranscriber(
    overlapProvider,
    new OpenAIProcessingOptions { TranscriptionWorkers = 1, TranscriptionConcurrencyPerWorker = 2 },
    NullLogger<ConcurrentChunkTranscriber>.Instance);
await overlapScheduler.Transcribe(
    [new AudioChunk("a", 0, 10), new AudioChunk("b", 10, 20)], null, CancellationToken.None);
Assert(
    overlapProvider.Calls.Count == 2 &&
        overlapProvider.Calls[0].Start < overlapProvider.Calls[1].End &&
        overlapProvider.Calls[1].Start < overlapProvider.Calls[0].End,
    "Two chunks did not transcribe concurrently even though the configured concurrency " +
    "allowed two in flight at once.");

var serializedProvider = new TimestampingFakeProvider(TimeSpan.FromMilliseconds(120));
var serializedScheduler = new ConcurrentChunkTranscriber(
    serializedProvider,
    new OpenAIProcessingOptions { TranscriptionWorkers = 1, TranscriptionConcurrencyPerWorker = 1 },
    NullLogger<ConcurrentChunkTranscriber>.Instance);
await serializedScheduler.Transcribe(
    [new AudioChunk("a", 0, 10), new AudioChunk("b", 10, 20)], null, CancellationToken.None);
Assert(
    serializedProvider.Calls[0].End <= serializedProvider.Calls[1].Start ||
        serializedProvider.Calls[1].End <= serializedProvider.Calls[0].Start,
    "A concurrency of one still let two chunks overlap, which means the overlap assertion " +
    "above proves nothing.");

// The raised A100 ceiling (four slots): four chunks submitted together must all run
// within the same window rather than any of them queuing behind another, which is what
// "headroom beyond two" actually has to mean if it is to be trusted under real load.
var fourWayProvider = new TimestampingFakeProvider(TimeSpan.FromMilliseconds(150));
var fourWayScheduler = new ConcurrentChunkTranscriber(
    fourWayProvider,
    new OpenAIProcessingOptions { TranscriptionWorkers = 1, TranscriptionConcurrencyPerWorker = 4 },
    NullLogger<ConcurrentChunkTranscriber>.Instance);
var fourWayStart = DateTime.UtcNow;
await fourWayScheduler.Transcribe(
    Enumerable.Range(0, 4).Select(i => new AudioChunk($"chunk-{i}", i * 10, i * 10 + 10)).ToArray(),
    null, CancellationToken.None);
var fourWayElapsed = DateTime.UtcNow - fourWayStart;
Assert(fourWayProvider.Calls.Count == 4, "Not all four submitted chunks were transcribed.");
Assert(
    fourWayElapsed < TimeSpan.FromMilliseconds(150 * 4),
    $"Four chunks at a concurrency of four took {fourWayElapsed.TotalMilliseconds:F0}ms, " +
    "close to four serial calls; none should have queued behind another.");

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

// A read-along EPUB attached to King Sorrow deliberately reports the audiobook's own
// duration and title so read-along knows which recording it belongs to. That borrowed
// evidence must never make the EPUB match as a second copy of the recording itself.
var kingSorrowEpub = editionBase with
{
    Sha256 = new string('c', 64), FileSize = 500, FileType = "epub",
};
Assert(!EditionMatch.SameRecording(editionBase, kingSorrowEpub),
    "An EPUB companion matched its own audiobook as the same recording.");
Assert(!EditionMatch.SameFileKind(editionBase, kingSorrowEpub),
    "An audiobook and an EPUB were reported as the same kind of file.");
Assert(EditionMatch.SameFileKind(editionBase, editionConverted),
    "Two audio files of different formats were not reported as the same kind of file.");
Assert(EditionMatch.SameFileKind(kingSorrowEpub, kingSorrowEpub with { Sha256 = new string('d', 64) }),
    "Two EPUB files were not reported as the same kind of file.");

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

// The prompt lists the labels Luna itself may emit. It is generated from the same source as
// the response schema, and this is what proves the generation still covers everything --
// narrower than the full enforced set, since profanity is excluded from what Luna may
// propose even though the app still offers its switches.
Assert(
    ContentTaxonomy.ModelEmittableLabels.Count == ContentTaxonomy.EnforcedLabels.Count - 4 &&
        !ContentTaxonomy.ModelEmittableLabels.Any(label => label.StartsWith("profanity_")),
    "ModelEmittableLabels no longer excludes exactly the four profanity labels from the " +
    "enforced set.");
foreach (var label in ContentTaxonomy.ModelEmittableLabels)
{
    Assert(OpenAIContentAnalysisProvider.AllowedLabelList.Contains(label, StringComparison.Ordinal),
        $"The analysis prompt does not mention the allowed label {label}.");
}
Assert(!OpenAIContentAnalysisProvider.AllowedLabelList.Contains("violence_mild", StringComparison.Ordinal),
    "The analysis prompt offers the model a label the policy excludes.");
foreach (var profanityLabel in new[]
    { "profanity_mild", "profanity_strong", "profanity_sexual", "profanity_slur" })
{
    Assert(
        !OpenAIContentAnalysisProvider.AllowedLabelList.Contains(profanityLabel, StringComparison.Ordinal),
        $"The analysis prompt still offers Luna the profanity label {profanityLabel}, which " +
        "should be deterministic-only.");
    Assert(
        ContentTaxonomy.EnforcedLabels.Contains(profanityLabel, StringComparer.Ordinal),
        $"{profanityLabel} was removed from EnforcedLabels, which would remove its switch " +
        "from the app even though profanity detection itself is unaffected.");
}

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

// Automatic cover art enrichment. The content-type gate only accepts what the storage
// endpoints themselves accept, and jpg is folded into the canonical jpeg spelling.
Assert(ITunesCoverArtProvider.NormalizeContentType("image/jpeg") == "image/jpeg",
    "A JPEG content type was not recognised.");
Assert(ITunesCoverArtProvider.NormalizeContentType("image/jpg") == "image/jpeg",
    "The nonstandard 'image/jpg' spelling was not folded into 'image/jpeg'.");
Assert(ITunesCoverArtProvider.NormalizeContentType("IMAGE/PNG") == "image/png",
    "A differently-cased content type was not recognised.");
Assert(ITunesCoverArtProvider.NormalizeContentType("text/html") is null,
    "An HTML error page's content type was accepted as an image.");
Assert(ITunesCoverArtProvider.NormalizeContentType(null) is null,
    "A missing content type was accepted as an image.");

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


// A filter report's position unit defaults to seconds for an existing client that sends
// none at all, and that has to keep meaning seconds so an already-shipped client's request
// body stays byte-identical.
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

    // Permissive rather than rejecting: a report is a one-off observation from someone who
    // heard a mistake, and discarding it over an unrecognised unit would lose the only
    // record that it happened.
    Assert(
        FilterReports.Validate(Guid.NewGuid(), audiobookReport with { PositionUnit = "furlongs" })
            ?.PositionUnit == FilterReportPositionUnits.Seconds,
        "An unrecognised position unit was not normalised to seconds.");
}

// Reader alignment: word-level density and recovery past a mismatch within one segment.
{
    // "the quick brown fox jumps over the lazy dog" -- an EPUB carrying exactly this text.
    var epubText = "the quick brown fox jumps over the lazy dog";

    // A segment reporting real per-word timing for the whole phrase. Every matched word
    // must get its own range, not one range spanning the segment's start to its end.
    TranscriptWord Word(string text, double start, double end) => new(text, start, end);
    var wordTimed = new TranscriptSegment(
        0, 4.5, "the quick brown fox jumps over the lazy dog",
        [
            Word("the", 0.0, 0.3), Word("quick", 0.3, 0.8), Word("brown", 0.8, 1.2),
            Word("fox", 1.2, 1.5), Word("jumps", 1.5, 2.0), Word("over", 2.0, 2.3),
            Word("the", 2.3, 2.5), Word("lazy", 2.5, 2.9), Word("dog", 2.9, 3.3),
        ]);
    var wordLevelRanges = ReaderAlignment.Create([wordTimed], epubText);
    Assert(
        wordLevelRanges.Count == 9,
        $"Expected one range per matched word (9), got {wordLevelRanges.Count}. Word-level " +
        "density is the whole point: a range per whole matched run would collapse this to one.");
    Assert(
        wordLevelRanges.All(range => range.EndTime - range.StartTime <= 1.0),
        "A range spans more than a single word's own duration, so it did not use the " +
        "transcript's real per-word timing.");
    // The fox's own word timing (1.2 to 1.5), not the segment's timing (0 to 4.5).
    var foxRange = wordLevelRanges.Single(range => epubText[range.StartCharacter] == 'f');
    Assert(
        Math.Abs(foxRange.StartTime - 1.2) < 0.001 && Math.Abs(foxRange.EndTime - 1.5) < 0.001,
        $"'fox' should be timed 1.2-1.5 from its own word timing, got " +
        $"{foxRange.StartTime}-{foxRange.EndTime}.");

    // A segment with no per-word timing at all (an older transcript, or a provider that
    // never reports it) still produces a range per word, interpolated across the segment.
    var noWordTiming = new TranscriptSegment(10, 14, "the quick brown fox jumps over the lazy dog");
    var interpolatedRanges = ReaderAlignment.Create([noWordTiming], epubText);
    Assert(
        interpolatedRanges.Count == 9,
        $"Expected one interpolated range per word (9) with no word timing, got " +
        $"{interpolatedRanges.Count}.");
    Assert(
        interpolatedRanges.All(range => range.StartTime >= 10 && range.EndTime <= 14),
        "An interpolated range fell outside its segment's own time span.");

    // A single mismatched word part-way through a segment must not cost the reliable words
    // on the far side of it. The EPUB carries "brown" but the transcript misheard it as
    // "brawn"; "the", "quick", "fox", "jumps" and everything after must still be found.
    var withMismatch = new TranscriptSegment(
        0, 4.5, "the quick brawn fox jumps over the lazy dog",
        [
            Word("the", 0.0, 0.3), Word("quick", 0.3, 0.8), Word("brawn", 0.8, 1.2),
            Word("fox", 1.2, 1.5), Word("jumps", 1.5, 2.0), Word("over", 2.0, 2.3),
            Word("the", 2.3, 2.5), Word("lazy", 2.5, 2.9), Word("dog", 2.9, 3.3),
        ]);
    var recoveredRanges = ReaderAlignment.Create([withMismatch], epubText);
    Assert(
        recoveredRanges.Any(range => epubText[range.StartCharacter] == 'f'),
        "'fox' was not recovered after a mismatched word earlier in the same segment, so a " +
        "single transcription slip cost every reliable word after it.");
    Assert(
        recoveredRanges.Any(range => epubText.Substring(range.StartCharacter, 3) == "dog"),
        "'dog', the last word, was not recovered after an earlier mismatch in the same segment.");
    Assert(
        !recoveredRanges.Any(range => epubText[range.StartCharacter] == 'b'),
        "The mismatched word ('brown' in the EPUB, heard as 'brawn') produced a range anyway.");

    // Ranges stay in increasing order of both time and character, matching the monotonic
    // cursor a filter mask or a binary search over these ranges depends on.
    for (var index = 1; index < wordLevelRanges.Count; index += 1)
    {
        Assert(
            wordLevelRanges[index].StartCharacter >= wordLevelRanges[index - 1].StartCharacter &&
            wordLevelRanges[index].StartTime >= wordLevelRanges[index - 1].StartTime,
            "Reader alignment ranges are not monotonic in both time and character.");
    }
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

// Word-level re-anchoring for every model-driven category, not only profanity. This is what
// stops a real event's timing from landing on an unrelated nearby sentence: a model proposes
// a wide range and a quote, and the quote is located in the transcript's own word timing
// before the model's numbers are ever trusted.
var reanchorSegment = new TranscriptSegment(
    500, 512, "She crossed her arms and took a stance, refusing to back down.", new[]
    {
        new TranscriptWord("She", 500.0, 500.3),
        new TranscriptWord("crossed", 500.3, 500.7),
        new TranscriptWord("her", 500.7, 500.9),
        new TranscriptWord("arms", 500.9, 501.3),
        new TranscriptWord("and", 501.3, 501.5),
        new TranscriptWord("took", 501.5, 501.8),
        new TranscriptWord("a", 501.8, 501.9),
        new TranscriptWord("stance,", 501.9, 502.4),
    });
var multiWordSpan = TranscriptWordLocator.FindPhrase(reanchorSegment.Words, "crossed her arms");
Assert(
    multiWordSpan is not null &&
        Math.Abs(multiWordSpan.StartTime - 500.3) < 0.001 &&
        Math.Abs(multiWordSpan.EndTime - 501.3) < 0.001,
    "A three-word phrase spanning consecutive transcript words was not located, or its span " +
    "did not match the words' own timing.");
Assert(
    TranscriptWordLocator.FindPhrase(reanchorSegment.Words, "took a firm stance") is null,
    "A phrase whose words do not actually appear in that order was located anyway. This is " +
    "the exact failure mode a real filter bug would produce: a model claims support for an " +
    "event using words the passage never contains, and a locator that finds a near-miss " +
    "instead of rejecting it would let the mistake straight through.");
Assert(
    TranscriptWordLocator.FindPhraseInSegments([reanchorSegment], "crossed her arms and took a stance")
        is { } fullPhrase && Math.Abs(fullPhrase.StartTime - 500.3) < 0.001,
    "A longer phrase spanning most of a segment's words was not located across the segment " +
    "list.");
Assert(
    TranscriptWordLocator.FindPhraseInSegments([reanchorSegment], "their union was blessed") is null,
    "A quote for content that does not appear anywhere in the supplied segments was located " +
    "anyway, which is exactly how an unrelated real event's quote could wrongly confirm a " +
    "completely different proposed range.");

// The character-offset path a narrated book's own passages use, which carries no word list
// at all -- only the passage's own text and its absolute starting offset.
var bookPassage = new TranscriptSegment(
    1200, 1260, "Their union was blessed by the elders that very night.");
Assert(
    TranscriptWordLocator.FindQuotedSubstring([bookPassage], "union was blessed")
        is { } substringSpan && substringSpan.StartTime == 1200 + "Their ".Length,
    "A quoted substring was not located at its exact character offset within the passage.");
Assert(
    TranscriptWordLocator.FindQuotedSubstring([bookPassage], "crossed her arms") is null,
    "A quoted substring absent from every supplied passage was located anyway.");

// The shared word-snap primitive every later boundary fix builds on.
{
    var snapWords = new TranscriptSegment(300, 310, "He paused before the door and knocked twice.", new[]
    {
        new TranscriptWord("He", 300.0, 300.2),
        new TranscriptWord("paused", 300.2, 300.6),
        new TranscriptWord("before", 300.6, 300.9),
        new TranscriptWord("the", 300.9, 301.0),
        new TranscriptWord("door", 301.0, 301.4),
        new TranscriptWord("and", 301.4, 301.6),
        new TranscriptWord("knocked", 301.6, 302.1),
        new TranscriptWord("twice.", 302.1, 302.5),
    });

    // An exact word match snaps to itself.
    var exact = TranscriptWordLocator.SnapToNearestWord([snapWords], 301.0, 301.4);
    Assert(
        exact is { } exactSpan && exactSpan.Start == 301.0 && exactSpan.End == 301.4,
        "A proposed boundary exactly on a word's own timing was not snapped to itself.");

    // A boundary sitting between two words snaps to the nearer one.
    var between = TranscriptWordLocator.SnapToNearestWord([snapWords], 300.7, 301.45);
    Assert(
        between is { } betweenSpan &&
            betweenSpan.Start == 300.6 && // 300.7 is nearer "before" (300.6-300.9)'s start than "the"'s
            betweenSpan.End == 301.4, // 301.45 is nearer "door"'s end (301.4) than "and"'s end (301.6)
        "A boundary between two words was not snapped to the nearer one.");

    // A boundary far outside the transcript still resolves to the nearest real word rather
    // than being left unsnapped or thrown.
    var beyond = TranscriptWordLocator.SnapToNearestWord([snapWords], -500, 9999);
    Assert(
        beyond is { } beyondSpan &&
            beyondSpan.Start == 300.0 && beyondSpan.End == 302.5,
        "A boundary far outside the transcript's own range was not clamped to its nearest word.");

    // No word list at all (a transcript saved before word timings existed) must fall back
    // gracefully rather than throw or invent a boundary.
    var noWords = new TranscriptSegment(300, 310, "He paused before the door.");
    Assert(
        TranscriptWordLocator.SnapToNearestWord([noWords], 301, 302) is null,
        "A transcript with no word timing returned a snap instead of falling back.");

    // Expansion moves one word boundary outward at a time within the cap, not as far as the
    // cap could reach: one second past "door"'s end (301.4) could reach all the way to
    // "knocked"'s end (302.1), but the nearest boundary beyond 301.4 is "and"'s own end
    // (301.6), and that is what a one-second allowance must snap to.
    var expanded = TranscriptWordLocator.ExpandAndSnap([snapWords], 301.0, 301.4, 1.0);
    Assert(
        expanded is { } expandedSpan && expandedSpan.End == 301.6,
        $"Expanding one second past a word's end did not snap to the nearest word boundary " +
        $"beyond it (got {expanded?.End}).");

    // An expansion cap that reaches no word at all (an isolated word with nothing within
    // range) falls back to the nearest word to the original point rather than to nothing.
    var isolated = new TranscriptSegment(400, 401, "Wait.", new[]
    {
        new TranscriptWord("Wait.", 400.0, 400.4),
    });
    var noRoomToExpand = TranscriptWordLocator.ExpandAndSnap([isolated], 400.0, 400.4, 0.1);
    Assert(
        noRoomToExpand is { } noRoomSpan &&
            noRoomSpan.Start == 400.0 && noRoomSpan.End == 400.4,
        "A tiny expansion cap with no other word nearby did not fall back to the original word.");
}

// Comparing two transcripts directly, for the case chapter structure and a retail identifier
// cannot reach: two files whose reported runtime disagrees for a reason that turns out to be
// a wrong client-reported duration rather than different audio. Built from the exact shape
// this was needed for on staging.
static PrivateTranscript FakeTranscript(string fullText, int segmentCount)
{
    var words = fullText.Split(' ');
    var perSegment = Math.Max(1, words.Length / segmentCount);
    var segments = new List<TranscriptSegment>();
    for (var index = 0; index * perSegment < words.Length; index += 1)
    {
        var slice = words.Skip(index * perSegment).Take(perSegment);
        segments.Add(new TranscriptSegment(index * 10.0, index * 10.0 + 9.5, string.Join(' ', slice)));
    }
    return new PrivateTranscript("1.0", "en", "test", DateTimeOffset.UtcNow, segments, true);
}

var sharedBookText = string.Join(' ', Enumerable.Range(0, 3000).Select(i => $"word{i}"));
var sameRecordingA = FakeTranscript(sharedBookText, 40);
var sameRecordingB = FakeTranscript(sharedBookText, 55);
Assert(
    TranscriptComparison.FindMismatch(sameRecordingA, sameRecordingB) is null,
    "Two transcriptions of the same text, chunked into a different number of segments, were "
        + "reported as not matching.");

var differentBookText = string.Join(' ', Enumerable.Range(0, 3000).Select(i => $"different{i}"));
var differentRecording = FakeTranscript(differentBookText, 45);
Assert(
    TranscriptComparison.FindMismatch(sameRecordingA, differentRecording) is not null,
    "Two transcripts of completely different text were reported as matching. A wrong "
        + "positive here would hand one recording's filter timings to a different book.");

// The real near-miss found on staging: two transcriptions of one book agreed word for word
// everywhere except one checkpoint, where an invented fantasy name was transcribed two
// different ways by ear -- "faera" in one, "pharah" in the other. One differing word out of
// twelve is exactly the ordinary noise two independent transcriptions produce and must not
// be reported as a mismatch. Substituted at word 2565, inside checkpoint 6's own window
// (2561-2573 of 3000 words), which is exactly where the real pair's difference landed --
// near the end of the book, past every other checkpoint.
var namedCharacterText = sharedBookText.Replace("word2565", "faera");
var otherSpellingText = sharedBookText.Replace("word2565", "pharah");
Assert(
    TranscriptComparison.FindMismatch(
        FakeTranscript(namedCharacterText, 40), FakeTranscript(otherSpellingText, 55)) is null,
    "A single differently-transcribed invented name was reported as a mismatch between two "
        + "transcripts that otherwise agree word for word across the entire book.");

// The one real, everyday case this exists to catch: a shared publisher intro is not enough
// evidence on its own, because every edition of a book from the same narrator carries it.
var sharedIntroOnly = FakeTranscript(
    "This is Audible. Graphic Audio. A movie in your mind. " + differentBookText, 45);
Assert(
    TranscriptComparison.FindMismatch(
        FakeTranscript("This is Audible. Graphic Audio. A movie in your mind. " + sharedBookText, 40),
        sharedIntroOnly) is not null,
    "Two transcripts sharing only a publisher intro, with different books after it, were "
        + "reported as matching.");

// Chapter structure as identity. This decides whether a converted copy of an already scanned
// recording finds its filters, and equally whether two unrelated books are handed each other's
// timings, so both directions are pinned.
var twelveMarks = Enumerable.Range(0, 12).Select(i => i * 1800).ToArray();
Assert(
    EditionMatch.ChapterStructureIdentifies(
        new EditionSignature(ChapterOffsetSeconds: twelveMarks),
        new EditionSignature(ChapterOffsetSeconds: twelveMarks)),
    "Twelve identical chapter marks were not accepted as the same recording.");

// A second's drift is re-encoding, not a different book.
var driftedMarks = twelveMarks.Select((value, index) => index == 5 ? value + 1 : value).ToArray();
Assert(
    EditionMatch.ChapterStructureIdentifies(
        new EditionSignature(ChapterOffsetSeconds: twelveMarks),
        new EditionSignature(ChapterOffsetSeconds: driftedMarks)),
    "A one-second difference in one chapter mark was treated as a different recording.");

// Too few marks cannot identify anything: two books of similar length with three evenly spaced
// parts would otherwise be handed each other's filters.
var threeMarks = new[] { 0, 1800, 3600 };
Assert(
    !EditionMatch.ChapterStructureIdentifies(
        new EditionSignature(ChapterOffsetSeconds: threeMarks),
        new EditionSignature(ChapterOffsetSeconds: threeMarks)),
    "Three chapter marks were accepted as identifying a recording. Eight is the floor.");

Assert(
    !EditionMatch.ChapterStructureIdentifies(
        new EditionSignature(ChapterOffsetSeconds: twelveMarks),
        new EditionSignature(ChapterOffsetSeconds: twelveMarks.Take(11).ToArray())),
    "Structures of different lengths were accepted as the same recording.");

Assert(
    !EditionMatch.ChapterStructureIdentifies(
        new EditionSignature(ChapterOffsetSeconds: twelveMarks),
        new EditionSignature(ChapterOffsetSeconds: twelveMarks.Select(v => v + 600).ToArray())),
    "Marks ten minutes apart were accepted as the same recording.");

Assert(
    !EditionMatch.ChapterStructureIdentifies(new EditionSignature(), new EditionSignature()),
    "Two signatures with no chapter marks at all were accepted as the same recording.");

// Migration 027 added the shared filter_reports.position_unit column, which every filter
// report -- audio or attached-text -- now carries. Additive only, checked here because the
// migration is already applied in real environments and must never be hand-edited: a drop
// or delete would change what an existing client sees.
{
    var migrationsDirectory = FindMigrationsDirectory();
    var migrationPath = Path.Combine(migrationsDirectory, "027_epub_narration.sql");
    Assert(File.Exists(migrationPath), "Migration 027 is missing.");
    var migrationSql = File.ReadAllText(migrationPath);
    foreach (var forbidden in new[] { "drop table", "delete from", "truncate", "drop column" })
    {
        Assert(
            !migrationSql.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
            $"Migration 027 must stay additive, but it contains '{forbidden}'.");
    }
    Assert(
        migrationSql.Contains($"'{FilterReportPositionUnits.Seconds}'", StringComparison.Ordinal) &&
        migrationSql.Contains($"'{FilterReportPositionUnits.CharacterOffset}'", StringComparison.Ordinal),
        "The position_unit constraint no longer permits both values FilterReportPositionUnits " +
        "defines, so a normalised report could not be written.");
}

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

/// <summary>The minimum needed to construct AudioChoiceDataPaths against a temp directory.</summary>
sealed class FakeWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
{
    public string ContentRootPath { get; set; } = contentRootPath;
    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "AudioChoice.Api.ContractTests";
    public string WebRootPath { get; set; } = contentRootPath;
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.NullFileProvider();
    public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.NullFileProvider();
}

/// <summary>
/// Answers Luna, Terra and Sol calls with fixed, recognizable decisions rather than a real
/// model, so the end-to-end pipeline test proves the scanner's own routing and boundary
/// logic without depending on model behavior at all.
/// </summary>
sealed class FixtureAnalysisModelClient : IAnalysisModelClient
{
    public string ProviderName => "fixture";
    public int LunaCallCount { get; private set; }
    public int TerraCallCount { get; private set; }
    public int SolCallCount { get; private set; }

    public Task<AnalysisModelResponse> CompleteJson(
        string model,
        string input,
        string schemaName,
        System.Text.Json.Nodes.JsonObject schema,
        CancellationToken cancellationToken)
    {
        var json = schemaName switch
        {
            "audiochoice_scan_events" => RespondToLuna(input),
            "audiochoice_violence_verification" => """{"candidates":[]}""",
            "audiochoice_scene_verification" => RespondToSceneVerification(model, input),
            _ => throw new InvalidOperationException($"Unexpected schema {schemaName}."),
        };
        return Task.FromResult(new AnalysisModelResponse(json));
    }

    /// <summary>
    /// Luna's fixed answer: the fixture sexual scene, whichever batch(es) it falls in,
    /// reported as one implied-activity event plus the required accompanying complete-scene
    /// event -- exactly the pairing the real prompt requires ("ALWAYS emit
    /// sexual_complete_scene alongside..."). A separate trigger phrase reports a
    /// sexual_violence candidate instead, mutually exclusive with the consensual pairing, the
    /// same way Luna's real prompt requires. Never reports profanity (Luna no longer may) or
    /// graphic violence for the fixture's ordinary argument.
    /// </summary>
    private string RespondToLuna(string input)
    {
        LunaCallCount += 1;
        if (input.Contains("refused to let go", StringComparison.Ordinal))
        {
            return """
            {"events":[
              {"label":"sexual_violence","startTime":10,"endTime":30,"confidence":0.8,
               "safeDescription":"Sexual violence is described","profanityWord":null,
               "quote":"refused to let go"}
            ]}
            """;
        }
        if (!input.Contains("kissed him slowly", StringComparison.Ordinal))
        {
            return """{"events":[]}""";
        }
        return """
        {"events":[
          {"label":"sexual_implied_activity","startTime":10,"endTime":30,"confidence":0.8,
           "safeDescription":"An intimate encounter is implied","profanityWord":null,
           "quote":"kissed him slowly"},
          {"label":"sexual_complete_scene","startTime":10,"endTime":30,"confidence":0.8,
           "safeDescription":"A sustained intimate encounter","profanityWord":null,
           "quote":"kissed him slowly"}
        ]}
        """;
    }

    /// <summary>
    /// Terra's fixed answer: accepts the scene at 0.97 confidence -- above
    /// SolEscalationConfidenceThreshold (0.95) -- so the Sol dispatch gate keeps it from
    /// ever reaching Sol, which is exactly the routing this test exists to prove. For a
    /// sexual_violence review (detected the same way the real prompt is written: it names
    /// the sexual-violence skip range explicitly), also reports nonconsensualEvidence=true,
    /// since this fixture's only violence candidate is meant to confirm.
    /// </summary>
    private string RespondToSceneVerification(string model, string input)
    {
        if (model == "gpt-5.6-sol") SolCallCount += 1;
        else TerraCallCount += 1;

        var candidateKeyStart = input.IndexOf("\"candidateKey\":\"", StringComparison.Ordinal);
        var candidateKey = "unknown";
        if (candidateKeyStart >= 0)
        {
            var valueStart = candidateKeyStart + "\"candidateKey\":\"".Length;
            var valueEnd = input.IndexOf('"', valueStart);
            if (valueEnd > valueStart) candidateKey = input[valueStart..valueEnd];
        }

        var isSexualViolenceLane = input.Contains("sexual-violence skip range", StringComparison.Ordinal);
        var nonconsensualEvidence = isSexualViolenceLane ? "true" : "false";
        var quote = isSexualViolenceLane ? "refused to let go" : "crossed the room and kissed";
        var safeDescription = isSexualViolenceLane
            ? "Sexual violence is described"
            : "Sustained consensual sexual activity";

        return $$"""
        {"candidates":[
          {"candidateKey":"{{candidateKey}}","accepted":true,"needsEscalation":false,
           "directSexualActEvidence":true,"sustainedBeyondKissing":true,
           "nonconsensualEvidence":{{nonconsensualEvidence}},
           "startTime":10,"endTime":29,"confidence":0.97,
           "safeDescription":"{{safeDescription}}",
           "quote":"{{quote}}"}
        ]}
        """;
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

/// <summary>
/// Exercises the pre-materialized path (<c>ScanPipeline.ProcessMaterialized</c>), which is
/// what production actually runs. Two chunks so the offset applied to the second one's
/// words is not indistinguishable from a bug that only shifts the first chunk correctly.
/// </summary>
sealed class TwoChunkMaterializedAudioChunker : IAudioChunker, IPreMaterializedAudioChunker
{
    public IAsyncEnumerable<AudioChunk> CreateChunks(
        string audioFilePath, CancellationToken cancellationToken) =>
        throw new NotSupportedException("This fake only exercises the materialized path.");

    public Task<IReadOnlyList<AudioChunk>> Materialize(
        string audioFilePath, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AudioChunk>>(
            [new AudioChunk("chunk-0", 0, 600), new AudioChunk("chunk-1", 600, 1200)]);
}

sealed class ChunkRelativeTranscriptionProvider : ITranscriptionProvider
{
    public string ModelName => "chunk-relative-fake";

    // Whisper reports word timings relative to the chunk it was handed, always starting near
    // zero regardless of where that chunk sits in the audiobook. Real transcription behaves
    // exactly the same way; this fake exists to make that shape reproducible in a test.
    public Task<IReadOnlyList<TranscriptSegment>> Transcribe(
        AudioChunk chunk, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TranscriptSegment>>(
            [new TranscriptSegment(1, 2, "word", [new TranscriptWord("word", 1, 2)])]);
}

/// <summary>Returns one fixed JSON body for every request, for testing an HttpClient-based provider without a real network call.</summary>
sealed class FakeHttpMessageHandler(string responseJson) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
        });
}

sealed class FakeTranscriptionProvider : ITranscriptionProvider
{
    public string ModelName => "fake-transcriber";

    public Task<IReadOnlyList<TranscriptSegment>> Transcribe(
        AudioChunk chunk,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TranscriptSegment>>(
            [new TranscriptSegment(0, 5, "test", [new TranscriptWord("test", 0, 5)])]);
}

/// <summary>
/// Records the wall-clock window of every call it receives, which is what proves two
/// chunks actually ran at the same time rather than merely being scheduled without error.
/// </summary>
sealed class TimestampingFakeProvider(TimeSpan delay) : ITranscriptionProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<(DateTime Start, DateTime End)> _calls = new();
    public string ModelName => "timestamping-test-model";
    public IReadOnlyList<(DateTime Start, DateTime End)> Calls => _calls.ToArray();

    public async Task<IReadOnlyList<TranscriptSegment>> Transcribe(
        AudioChunk chunk, CancellationToken cancellationToken)
    {
        var start = DateTime.UtcNow;
        await Task.Delay(delay, cancellationToken);
        _calls.Enqueue((start, DateTime.UtcNow));
        return [new TranscriptSegment(0, 1, chunk.FilePath)];
    }
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
