using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Processing;

public sealed class OpenAIContentAnalysisProvider(
    IAnalysisModelClient modelClient,
    OpenAIProcessingOptions options,
    AudioChoice.Api.Services.AudioChoiceDataPaths dataPaths,
    ILogger<OpenAIContentAnalysisProvider> logger)
    : IContentAnalysisProvider, ITextContentAnalysisProvider
{
    // Bump this whenever the baseline classification policy changes so cached batch
    // answers cannot silently reintroduce events produced under an older policy.
    private const string BaseAnalysisPromptVersion = "2.9-verified-violence";
    private const string SceneVerificationVersion = "3.4-discreet-descriptions";
    private const string SceneEscalationVersion = "3.3-discreet-descriptions";
    private readonly string _checkpointFolder = dataPaths.AnalysisCheckpoints;
    public string ScannerVersion => options.ScannerVersion;

    /// <summary>
    /// The confidence floor as it applies to the mode actually running.
    /// </summary>
    /// <remarks>
    /// Lambda-first mode has no model first pass: the keyword cues are the only evidence
    /// there is, and they are emitted at 0.35 by design. Applying the floor there would
    /// silence the entire scanner rather than tighten it.
    /// </remarks>
    private double EffectiveMinimumConfidence =>
        options.LambdaFirstPassEnabled ? 0 : options.MinimumEventConfidence;

    public async Task<IReadOnlyList<ScanEvent>> Analyze(
        IReadOnlyList<TranscriptSegment> segments,
        Action<double>? reportProgress,
        CancellationToken cancellationToken)
    {
        var events = new List<ScanEvent>();
        // Exact profane words always count: there is no judgement in matching a literal
        // word, and the apps need the word itself to group its occurrences under one switch.
        var deterministic = DeterministicContentDetector.DetectProfanity(segments).ToList();

        // Keyword cues are only a source of events where nothing else reads the passage.
        // With Luna reading the whole transcript, adding them produced events its own
        // instructions rule out -- a single "bloody" reported as graphic violence, "high"
        // as intoxication, "pot" as a drug reference -- at a confidence nothing checked.
        if (options.LambdaFirstPassEnabled)
        {
            deterministic.AddRange(DeterministicContentDetector.DetectCategoryCues(segments));
        }

        for (var index = 0; index < deterministic.Count; index += 1)
        {
            var item = deterministic[index];
            AddEvent(events, item.Label, item.StartTime, item.EndTime,
                item.Confidence, item.SafeDescription, item.ProfanityWord,
                item.StartTime, item.EndTime, $"deterministic-{index}");
        }
        logger.LogInformation(
            "Deterministic analysis found {EventCount} events in {SegmentCount} transcript segments " +
            "(category cues {CueMode}).",
            deterministic.Count,
            segments.Count,
            options.LambdaFirstPassEnabled ? "included" : "used for review selection only");

        if (options.LambdaFirstPassEnabled)
        {
            logger.LogInformation(
                "Lambda initial scan progress: 0/{TotalSegments} transcript segments.",
                segments.Count);
            var localCandidates = DeterministicContentDetector.CandidateWindows(segments);
            logger.LogInformation(
                "Lambda initial scan completed: {ProcessedSegments}/{TotalSegments} segments; " +
                "{CandidateCount} sexual-content candidate windows found.",
                segments.Count, segments.Count, localCandidates.Count);

            for (var index = 0; index < localCandidates.Count; index += 1)
            {
                var range = localCandidates[index];
                var window = segments.Skip(range.StartIndex)
                    .Take(range.EndExclusive - range.StartIndex).ToArray();
                if (window.Length == 0) continue;
                AddEvent(events, "sexual_references",
                    window[0].StartTime, window[^1].EndTime, .35,
                    "Sexual references or suggestive dialogue detected", null,
                    window[0].StartTime, window[^1].EndTime,
                    $"lambda-candidate-{index}");
            }

            reportProgress?.Invoke(.75);
            var lambdaEvents = events
                .GroupBy(item => item.StableKey, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(item => item.Confidence).First())
                .OrderBy(item => item.StartTime)
                .ToArray();
            var lambdaVerified = await VerifyCompleteSexualScenes(
                lambdaEvents, segments,
                progress => reportProgress?.Invoke(.75 + progress * .25),
                cancellationToken);
            var lambdaResult = SceneEventPostProcessor.Process(lambdaVerified, segments).ToArray();
            logger.LogInformation(
                "Lambda-first content analysis completed with {EventCount} events.",
                lambdaResult.Length);
            return UserFacingEventPostProcessor.Process(
                ReportCompleteSceneCoverage(lambdaResult, segments));
        }

        var batchSize = Math.Max(1, options.MaximumSegmentsPerAnalysisRequest);
        // Scene boundaries often cross request boundaries. A 50% overlap gives the model
        // enough preceding and following narrative context, while per-batch checkpoints
        // ensure an interrupted reanalysis resumes without paying for completed requests.
        var overlap = Math.Max(0, batchSize / 2);
        var step = Math.Max(1, batchSize - overlap);
        IReadOnlyList<(int StartIndex, int EndExclusive)> batchRanges = options.LocalCandidateFunnelEnabled
            ? DeterministicContentDetector.CandidateWindows(segments)
            : Enumerable.Range(0, (int)Math.Ceiling(segments.Count / (double)step))
                .Select(index => (StartIndex: index * step,
                    EndExclusive: Math.Min(segments.Count, index * step + batchSize)))
                .ToArray();

        if (options.LocalCandidateFunnelEnabled)
        {
            logger.LogInformation(
                "Local candidate funnel selected {WindowCount} model-review windows from {SegmentCount} transcript segments.",
                batchRanges.Count, segments.Count);
        }

        var batchResults = await RunContentBatches(batchRanges, segments, reportProgress, cancellationToken);
        foreach (var batchResult in batchResults.OrderBy(item => item.Index))
        {
            var batchNumber = batchResult.Index + 1;
            var batch = batchResult.Batch;
            var classified = batchResult.Payload;
            var batchStart = batch.Min(item => item.StartTime);
            var batchEnd = batch.Max(item => item.EndTime);
            logger.LogInformation(
                "Content analysis batch {BatchNumber} returned {EventCount} events for {SegmentCount} segments.",
                batchNumber,
                classified.Events.Count,
                batch.Count);

            var admitted = 0;
            foreach (var item in classified.Events)
            {
                if (AddEvent(events, item.Label, item.StartTime, item.EndTime,
                    item.Confidence, item.SafeDescription, item.ProfanityWord,
                    batchStart, batchEnd)) admitted += 1;
            }
            if (admitted != classified.Events.Count)
            {
                logger.LogInformation(
                    "Batch {BatchNumber} had {RejectedCount} of {EventCount} events rejected " +
                    "below the {Floor:F2} confidence floor.",
                    batchNumber, classified.Events.Count - admitted,
                    classified.Events.Count, options.MinimumEventConfidence);
            }
        }

        var uniqueEvents = events
            .GroupBy(item => item.StableKey, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.Confidence).First())
            .OrderBy(item => item.StartTime)
            .ToArray();
        // Do not allow broad/ordinary violence categories into a new filter profile.
        // The app's Violence switch is intentionally reserved for graphic material,
        // torture, violence involving children or animals, and suicide/self-harm.
        uniqueEvents = ApplyNarrowViolencePolicy(uniqueEvents);
        uniqueEvents = await VerifyGraphicViolence(uniqueEvents, segments, cancellationToken);
        var verifiedEvents = await VerifyCompleteSexualScenes(
            uniqueEvents, segments,
            progress => reportProgress?.Invoke(.75 + progress * .25),
            cancellationToken);
        var result = SceneEventPostProcessor.Process(verifiedEvents, segments).ToArray();
        result = ReportCompleteSceneCoverage(result, segments);

        if (segments.Count >= 500 && result.Length == 0 &&
            DeterministicContentDetector.ContainsObviousContent(segments))
        {
            throw new InvalidOperationException(
                "Content analysis produced an implausible empty result for a transcript containing explicit indicators.");
        }

        logger.LogInformation(
            "Content analysis completed with {EventCount} unique events.",
            result.Length);
        return UserFacingEventPostProcessor.Process(result);
    }

    /// <summary>
    /// Classifies passages measured in character offsets, for a book with no audiobook.
    /// </summary>
    /// <remarks>
    /// Deliberately written as its own method rather than by threading a coordinate-space
    /// flag through <see cref="Analyze"/>. Not one line of <c>Analyze</c> changes, so the
    /// scanning pipeline that produces the published catalogue is provably untouched by
    /// narration work. The cost is roughly thirty lines of loop glue duplicated below; the
    /// parts with real behaviour in them -- <c>RunContentBatches</c>, <c>AddEvent</c>,
    /// <c>ApplyNarrowViolencePolicy</c>, <c>UserFacingEventPostProcessor</c> -- are called,
    /// not copied, so the two paths cannot drift apart on taxonomy, confidence or wording.
    ///
    /// Four steps <c>Analyze</c> performs are absent, each because it is defined in seconds:
    /// scene verification (±30-second boundary clamps), <c>SceneEventPostProcessor</c>
    /// (45-second merge, 8-second padding, 30-second floor), complete-scene coverage
    /// reporting (which divides by an audiobook duration this book does not have), and the
    /// large-transcript plausibility check (whose 500-segment threshold counts something
    /// different here). <c>UserFacingEventPostProcessor</c> is kept: the app's category
    /// switches read the group and aggregate identifiers it assigns, and its only
    /// span-sensitive behaviour is a five-unit clustering gap that affects how events are
    /// labelled in aggregate, never where they begin or end.
    /// </remarks>
    public async Task<IReadOnlyList<ScanEvent>> AnalyzeCharacterOffsets(
        IReadOnlyList<TranscriptSegment> passages,
        Action<double>? reportProgress,
        CancellationToken cancellationToken)
    {
        if (passages.Count == 0) return [];

        var events = new List<ScanEvent>();

        // Literal word matches are exact in either coordinate space: a profane word is
        // present or it is not, and the offsets come straight from the passage that
        // contains it. This is also what gives every occurrence of one word the same
        // aggregate key, which is what lets the app group them under a single switch.
        var deterministic = DeterministicContentDetector.DetectProfanity(passages).ToList();
        for (var index = 0; index < deterministic.Count; index += 1)
        {
            var item = deterministic[index];
            AddEvent(events, item.Label, item.StartTime, item.EndTime,
                item.Confidence, item.SafeDescription, item.ProfanityWord,
                item.StartTime, item.EndTime, $"deterministic-text-{index}");
        }

        var batchSize = Math.Max(1, options.MaximumSegmentsPerAnalysisRequest);
        var overlap = Math.Max(0, batchSize / 2);
        var step = Math.Max(1, batchSize - overlap);
        var batchRanges = Enumerable
            .Range(0, (int)Math.Ceiling(passages.Count / (double)step))
            .Select(index => (StartIndex: index * step,
                EndExclusive: Math.Min(passages.Count, index * step + batchSize)))
            .ToArray();

        var batchResults = await RunContentBatches(
            batchRanges, passages, reportProgress, cancellationToken);
        foreach (var batchResult in batchResults.OrderBy(item => item.Index))
        {
            var batch = batchResult.Batch;
            var batchStart = batch.Min(item => item.StartTime);
            var batchEnd = batch.Max(item => item.EndTime);
            foreach (var item in batchResult.Payload.Events)
            {
                AddEvent(events, item.Label, item.StartTime, item.EndTime,
                    item.Confidence, item.SafeDescription, item.ProfanityWord,
                    batchStart, batchEnd);
            }
        }

        var uniqueEvents = events
            .GroupBy(item => item.StableKey, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.Confidence).First())
            .OrderBy(item => item.StartTime)
            .ToArray();
        uniqueEvents = ApplyNarrowViolencePolicy(uniqueEvents);

        logger.LogInformation(
            "Text content analysis completed with {EventCount} unique events across " +
            "{PassageCount} passages.",
            uniqueEvents.Length,
            passages.Count);
        return UserFacingEventPostProcessor.Process(uniqueEvents);
    }

    private async Task<IReadOnlyList<ContentBatchResult>> RunContentBatches(
        IReadOnlyList<(int StartIndex, int EndExclusive)> ranges,
        IReadOnlyList<TranscriptSegment> segments,
        Action<double>? reportProgress,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, options.ContentAnalysisConcurrency));
        var completed = 0;
        var tasks = ranges.Select((range, index) => ProcessContentBatch(
            index, range, segments, gate,
            () => reportProgress?.Invoke(Math.Clamp(
                Interlocked.Increment(ref completed) / (double)Math.Max(1, ranges.Count) * .75,
                0, .75)), cancellationToken));
        return await Task.WhenAll(tasks);
    }

    private async Task<ContentBatchResult> ProcessContentBatch(
        int index,
        (int StartIndex, int EndExclusive) range,
        IReadOnlyList<TranscriptSegment> segments,
        SemaphoreSlim gate,
        Action completed,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var batch = segments.Skip(range.StartIndex)
                .Take(range.EndExclusive - range.StartIndex).ToArray();
            var checkpointPath = CheckpointPath(batch);
            var classified = await LoadCheckpoint(checkpointPath, cancellationToken);
            if (classified is null)
            {
                classified = await AnalyzeBatch(batch, cancellationToken);
                await SaveCheckpoint(checkpointPath, classified, cancellationToken);
            }
            else
            {
                logger.LogInformation(
                    "Reused content-analysis checkpoint for batch {BatchNumber}; no API request was made.",
                    index + 1);
            }
            completed();
            return new ContentBatchResult(index, batch, classified);
        }
        finally { gate.Release(); }
    }

    private static ScanEvent[] ApplyNarrowViolencePolicy(IReadOnlyList<ScanEvent> events)
    {
        var excludedLabels = new[] { "violence_mild", "violence_intense", "violence_death" };
        var excludedEventIds = excludedLabels
            .Select(label => ContentTaxonomy.Mappings[label].EventID)
            .ToHashSet();
        return events.Where(item => !excludedEventIds.Contains(item.EventID)).ToArray();
    }

    /// <summary>
    /// Records how much of the audiobook the verified scenes cover. Suppresses nothing.
    /// </summary>
    /// <remarks>
    /// This previously discarded every complete-scene range when there were more than 25 of
    /// them, or when they covered more than a fifth of the runtime, logging an error and
    /// returning success. The thresholds were meant to catch a runaway verifier, but an
    /// explicit romance genuinely has more than 25 scenes, and a fifth of the runtime is an
    /// ordinary amount for one. So the guard fired hardest on the books the filters exist
    /// for, removed every broad skip, and told the listener nothing -- leaving them to hear
    /// the content they had asked to have removed.
    ///
    /// Three passes already have to agree before a range gets this far: Luna proposes it,
    /// Terra confirms a sustained act, and Sol reviews it again, all at 0.85 confidence or
    /// better. A result that survives all of that is evidence about the book, not a fault to
    /// be corrected by throwing it away. Density is logged so an actually broken run is still
    /// visible after the fact.
    /// </remarks>
    private ScanEvent[] ReportCompleteSceneCoverage(
        IReadOnlyList<ScanEvent> events,
        IReadOnlyList<TranscriptSegment> segments)
    {
        if (segments.Count == 0) return events.ToArray();
        var mapping = ContentTaxonomy.Mappings["sexual_complete_scene"];
        var completeScenes = events.Where(item => item.EventID == mapping.EventID).ToArray();
        if (completeScenes.Length == 0) return events.ToArray();

        var audiobookDuration = Math.Max(1, segments.Max(item => item.EndTime) -
            segments.Min(item => item.StartTime));
        var skippedDuration = completeScenes.Sum(item => Math.Max(0, item.EndTime - item.StartTime));
        var share = skippedDuration / audiobookDuration;

        // Warn rather than suppress. A very high share is worth a human look, but the
        // listener still gets the filtering they asked for in the meantime.
        if (completeScenes.Length > 25 || share > .20)
        {
            logger.LogWarning(
                "Unusually dense sexual-scene result: {SceneCount} scenes covering " +
                "{SkippedSeconds:F0} of {AudiobookSeconds:F0} seconds ({Share:P0}). " +
                "All ranges are retained; review the edition if this looks wrong.",
                completeScenes.Length, skippedDuration, audiobookDuration, share);
        }
        else
        {
            logger.LogInformation(
                "Sexual-scene coverage: {SceneCount} scenes over {Share:P0} of the audiobook.",
                completeScenes.Length, share);
        }
        return events.ToArray();
    }

    private string CheckpointPath(IReadOnlyList<TranscriptSegment> segments)
    {
        // Keep the expensive high-recall pass reusable across scanner releases. Scanner
        // 2.3 adds an independent verifier but intentionally reuses the completed 2.2
        // candidate checkpoints instead of paying to generate them again.
        var material = $"{options.AnalysisModel}|{BaseAnalysisPromptVersion}|" +
            JsonSerializer.Serialize(segments);
        var key = Hash(material);
        return Path.Combine(_checkpointFolder, $"{key}.json");
    }

    private static async Task<AnalysisPayload?> LoadCheckpoint(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var input = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<AnalysisPayload>(
                input, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task SaveCheckpoint(
        string path,
        AnalysisPayload payload,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        await using (var output = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(
                output, payload, cancellationToken: cancellationToken);
        }
        File.Move(temporary, path, overwrite: true);
    }

    /// <returns>False when the event was rejected and will not reach a listener.</returns>
    private bool AddEvent(
        ICollection<ScanEvent> events,
        string label,
        double start,
        double end,
        double confidence,
        string? safeDescription,
        string? profanityWord,
        double batchStart,
        double batchEnd,
        string? stableSuffix = null)
    {
        if (!ContentTaxonomy.Mappings.TryGetValue(label, out var mapping))
        {
            // Previously a silent return. A label the taxonomy does not know means the prompt
            // or schema moved ahead of it, and the detection is being dropped -- which is
            // exactly the kind of thing that must not happen quietly.
            logger.LogError(
                "Discarded a detection with unknown taxonomy label {Label}. The analysis " +
                "schema and ContentTaxonomy have diverged.", label);
            return false;
        }

        // Enforces the floor the prompt already states. Nothing downstream reads confidence,
        // so anything admitted here is presented to a listener with full authority.
        if (confidence < EffectiveMinimumConfidence)
        {
            return false;
        }
        var startTime = Math.Clamp(start, batchStart, batchEnd);
        var endTime = Math.Clamp(end, startTime, batchEnd);
        var description = label.StartsWith("profanity_", StringComparison.Ordinal)
            ? "Profanity detected"
            : SafeDescriptionForEvent(label, safeDescription);
        events.Add(new ScanEvent(
            Guid.NewGuid(), startTime, endTime, mapping.CategoryID, mapping.GroupID,
            mapping.EventID, Math.Clamp(confidence, 0, 1),
            StableEventKey(mapping, startTime, endTime, description, stableSuffix), description,
            AggregateKey(profanityWord), CensorWord(profanityWord)));
        return true;
    }

    public static string SafeDescriptionForEvent(string label, string? supplied)
    {
        var isInternalDescription =
            string.Equals(supplied, "Local Lambda content cue", StringComparison.Ordinal) ||
            string.Equals(supplied, "Lambda sexual-content candidate window", StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(supplied) && !isInternalDescription &&
            !ContainsUncleanDetail(supplied) && !IsTooVagueForUser(supplied))
        {
            return SafeDescription(supplied);
        }

        return label switch
        {
            "sexual_suggestive_dialogue" => "Suggestive dialogue or innuendo occurs",
            "sexual_references" => "A sexual reference is made",
            "sexual_nudity" => "A character removes clothing or is described without clothing",
            "sexual_implied_activity" => "An intimate encounter is implied",
            "sexual_explicit_activity" => "Characters are described in an intimate encounter",
            "sexual_complete_scene" => "Characters are described in a sustained intimate encounter",
            "violence_graphic" => "Graphic violence described",
            "violence_torture" => "Torture described",
            "violence_children" => "Violence involving children described",
            "violence_animals" => "Violence involving animals described",
            "substance_alcohol_use" => "Alcohol use described",
            "substance_intoxication" => "Intoxication described",
            "substance_drug_reference" => "Drug reference detected",
            "substance_drug_use" => "Drug use described",
            "substance_abuse_overdose" => "Substance abuse or overdose described",
            "blasphemy_religious_profanity" => "Religious profanity detected",
            "blasphemy_statement" => "Blasphemous statement detected",
            "self_harm_reference" => "Self-harm reference detected",
            "self_harm_suicidal_thoughts" => "Suicidal thoughts described",
            "self_harm_suicide_attempt" => "Suicide attempt described",
            "self_harm_depiction" => "Self-harm depicted",
            _ => "Content event detected"
        };
    }

    private static bool ContainsUncleanDetail(string value)
    {
        var uncleanTerms = new[]
        {
            "breast", "nipple", "penis", "vagina", "clitoris", "genital", "buttock",
            "anus", "intercourse", "penetrat", "oral sex", "thrust", "squeez", "grop",
            "fondl", "severed", "bloodied", "dismember", "decapitat", "behead", "gore",
            "guts", "intestine", "mutilat", "wet impact", "slit", "cut their wrist",
            "cut his wrist", "cut her wrist", "cut my wrist", "stabbed their own",
            "stabbed his own", "stabbed her own", "stabbed my own", "hanging themself",
            "hanging himself", "hanging herself"
        };
        return uncleanTerms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTooVagueForUser(string value)
    {
        var normalized = value.Trim();
        return normalized.Equals("Content event detected", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("Sexual activity described", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("Intimate positioning is described", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("Related sexual content", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("Related content", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<AnalysisPayload> AnalyzeBatch(
        IReadOnlyList<TranscriptSegment> segments,
        CancellationToken cancellationToken)
    {
        var input = BuildInput(segments);
        var response = await modelClient.CompleteJson(
            options.AnalysisModel,
            input,
            "audiochoice_scan_events",
            AnalysisResponseSchema(),
            cancellationToken);
        RecordUsage(options.AnalysisModel, response);

        return ReadPayload<AnalysisPayload>(response.Json, "Content analysis")
            ?? throw new InvalidOperationException(
                "Content analysis returned no structured result.");
    }


    /// <summary>
    /// Confirms that each proposed graphic-violence or torture event actually describes injury.
    /// </summary>
    /// <remarks>
    /// Violence was the only high-volume label with no second opinion. Sexual scenes have had
    /// two review passes for a long time; violence had the first pass's word and nothing else,
    /// and the first pass is the cheapest model in the pipeline.
    ///
    /// That gap was invisible while one model family did the classifying and became obvious with
    /// another. A six-hour book produced 163 graphic-violence events, roughly 28 an hour, and a
    /// separate probe had already had the same model call a slammed door graphic violence at
    /// 0.85 confidence. Two model families over-applying the same label the same way is not a
    /// model problem, it is an unreviewed label.
    ///
    /// Sent to the verification model rather than the first-pass one, deliberately. The whole
    /// finding here is that the cheap model cannot make this judgement, so asking it again would
    /// only produce the same answer more expensively.
    ///
    /// Anything the verifier does not confirm is dropped rather than downgraded. A listener
    /// asking not to hear injury described is not helped by a quieter version of the same skip,
    /// and the narrower violence labels were already removed by policy.
    /// </remarks>

    /// <summary>
    /// Reads a model's JSON leniently, and says what it received when it cannot.
    /// </summary>
    /// <remarks>
    /// Models send numbers as quoted strings, and a strict read of one field discards a whole
    /// book's analysis after every model call in it has been paid for. AllowReadingFromString
    /// covers that case generally, rather than one field at a time as each is discovered.
    ///
    /// The payload is logged on failure, truncated. A deserialization error naming a byte offset
    /// and nothing else is not diagnosable, and the alternative was guessing at which field
    /// moved -- once per rebuild, per book, at twenty minutes a guess.
    /// </remarks>
    private T? ReadPayload<T>(string json, string what)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, LenientJson);
        }
        catch (JsonException error)
        {
            logger.LogError(
                "{What} returned JSON that could not be read: {Message}. Payload: {Payload}",
                what, error.Message, json.Length > 900 ? json[..900] : json);
            throw;
        }
    }

    private static readonly JsonSerializerOptions LenientJson = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        PropertyNameCaseInsensitive = true
    };

    private async Task<ScanEvent[]> VerifyGraphicViolence(
        IReadOnlyList<ScanEvent> events,
        IReadOnlyList<TranscriptSegment> segments,
        CancellationToken cancellationToken)
    {
        var graphic = ContentTaxonomy.Mappings["violence_graphic"].EventID;
        var torture = ContentTaxonomy.Mappings["violence_torture"].EventID;
        var subject = events
            .Where(item => item.EventID == graphic || item.EventID == torture)
            .OrderBy(item => item.StartTime)
            .ToArray();
        if (subject.Length == 0) return events.ToArray();

        var kept = events
            .Where(item => item.EventID != graphic && item.EventID != torture)
            .ToList();

        // Several candidates per request. One each would multiply a busy book's request count by
        // the very thing being measured, and Bedrock's rate limit is already the tightest
        // constraint on a long book.
        var batches = subject.Chunk(ViolenceVerificationBatchSize).ToArray();
        if (batches.Length > options.MaximumSceneVerificationRequestsPerJob)
        {
            logger.LogWarning(
                "Graphic-violence verification would need {BatchCount} requests, above the " +
                "{Limit} limit. Proposed violence is kept unverified for this job.",
                batches.Length, options.MaximumSceneVerificationRequestsPerJob);
            return events.ToArray();
        }

        var confirmed = new HashSet<string>(StringComparer.Ordinal);
        using var gate = new SemaphoreSlim(Math.Max(1, options.SceneVerificationConcurrency));
        var decisions = await Task.WhenAll(batches.Select(async batch =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                return await VerifyViolenceBatch(batch, segments, cancellationToken);
            }
            finally { gate.Release(); }
        }));
        foreach (var key in decisions.SelectMany(item => item)) confirmed.Add(key);

        var survivors = subject.Where(item => confirmed.Contains(item.StableKey)).ToArray();
        logger.LogInformation(
            "Graphic-violence verification kept {Kept} of {Proposed} proposed events across " +
            "{Requests} requests.",
            survivors.Length, subject.Length, batches.Length);

        kept.AddRange(survivors);
        return kept.OrderBy(item => item.StartTime).ToArray();
    }

    /// <returns>The stable keys the verifier confirmed as describing injury.</returns>
    private async Task<IReadOnlyList<string>> VerifyViolenceBatch(
        IReadOnlyList<ScanEvent> batch,
        IReadOnlyList<TranscriptSegment> segments,
        CancellationToken cancellationToken)
    {
        var candidates = batch.Select(item => new
        {
            candidateKey = item.StableKey,
            startTime = item.StartTime,
            endTime = item.EndTime,
            // A little either side, because a description of a wound often begins in the line
            // before the one that named the act.
            segments = segments
                .Where(segment => segment.EndTime >= item.StartTime - 10 &&
                    segment.StartTime <= item.EndTime + 10)
                .ToArray()
        }).ToArray();

        var input = """
Decide, for each candidate, whether the narration dwells on the physical detail of a body being
damaged. That is the only question.

Confirm it when the passage describes flesh being cut, torn or opened; blood flowing or pooling;
bones breaking; organs, entrails or brain matter; a limb or head severed; or a wound described
closely enough that a listener pictures the injury itself.

Do not confirm an act of violence stated without that detail. A punch, a slap, a shove, a
slammed door, a stabbing or shooting reported without describing the wound, a battle or duel, a
threat, someone being hurt or killed, a body discovered, bruises, scars, blood mentioned in
passing, medical treatment, pain, an injury's aftermath, grief, or fantasy peril are all not
confirmed. Captivity and beating are not confirmed either, however unpleasant: a character tied
to a chair and punched does not qualify.

Most fight scenes are not confirmed. If you are weighing whether the description is detailed
enough, it is not. Answer for every candidateKey.

Candidates:
""" + JsonSerializer.Serialize(candidates);

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("candidates"),
            ["properties"] = new JsonObject
            {
                ["candidates"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = new JsonArray(
                            "candidateKey", "dwellsOnPhysicalDamage", "confidence"),
                        ["properties"] = new JsonObject
                        {
                            ["candidateKey"] = new JsonObject { ["type"] = "string" },
                            ["dwellsOnPhysicalDamage"] = new JsonObject { ["type"] = "boolean" },
                            ["confidence"] = new JsonObject
                            {
                                ["type"] = "number", ["minimum"] = 0, ["maximum"] = 1
                            }
                        }
                    }
                }
            }
        };

        try
        {
            var response = await modelClient.CompleteJson(
                options.SceneVerificationModel, input,
                "audiochoice_violence_verification", schema, cancellationToken);
            RecordUsage(options.SceneVerificationModel, response);
            var payload = ReadPayload<ViolenceVerificationPayload>(response.Json, "Violence verification");
            return payload?.Candidates
                .Where(item => item.DwellsOnPhysicalDamage &&
                    item.Confidence >= options.MinimumEventConfidence)
                .Select(item => item.CandidateKey)
                .ToArray() ?? [];
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A verifier that cannot answer must not silently delete a listener's protection.
            // Keeping the batch means over-filtering for this book, which is recoverable by
            // rescanning; dropping it means content plays that somebody asked to remove.
            logger.LogError(
                error,
                "Graphic-violence verification failed for {Count} candidates; keeping them " +
                "unverified rather than discarding protection.",
                batch.Count);
            return batch.Select(item => item.StableKey).ToArray();
        }
    }

    /// <summary>Candidates per verification request, balancing cost against rate limits.</summary>
    private const int ViolenceVerificationBatchSize = 8;

    private sealed record ViolenceVerificationPayload(
        [property: JsonPropertyName("candidates")]
        IReadOnlyList<ViolenceVerificationDecision> Candidates);

    private sealed record ViolenceVerificationDecision(
        [property: JsonPropertyName("candidateKey")] string CandidateKey,
        [property: JsonPropertyName("dwellsOnPhysicalDamage")] bool DwellsOnPhysicalDamage,
        [property: JsonPropertyName("confidence")] double Confidence);

    private async Task<IReadOnlyList<ScanEvent>> VerifyCompleteSexualScenes(
        IReadOnlyList<ScanEvent> events,
        IReadOnlyList<TranscriptSegment> segments,
        Action<double>? reportProgress,
        CancellationToken cancellationToken)
    {
        var mapping = ContentTaxonomy.Mappings["sexual_complete_scene"];
        var sexualEventIDs = new[]
        {
            "sexual_suggestive_dialogue", "sexual_references", "sexual_nudity",
            "sexual_implied_activity", "sexual_explicit_activity", "sexual_complete_scene"
        }.Select(label => ContentTaxonomy.Mappings[label].EventID).ToHashSet();
        var candidates = events
            .Where(item => sexualEventIDs.Contains(item.EventID))
            .OrderBy(item => item.StartTime)
            .Select(item => new SceneVerificationCandidate(
                item.StableKey,
                item.StartTime,
                item.EndTime,
                segments.Where(segment =>
                    segment.EndTime >= item.StartTime - 20 &&
                    segment.StartTime <= item.EndTime + 20).ToArray()))
            .ToArray();

        var retained = events.Where(item => item.EventID != mapping.EventID).ToList();
        if (candidates.Length == 0)
        {
            reportProgress?.Invoke(1);
            return retained;
        }

        // Every sexual-content candidate goes through Terra. Coalesce overlapping
        // or nearby high-recall events first: the first pass can emit several labels
        // for the same scene, and sending each label separately wastes requests and
        // can trip the spending guard without improving recall.
        var verificationCandidates = CoalesceSceneCandidates(candidates);
        logger.LogInformation(
            "Coalesced {RawCandidateCount} sexual candidates into {VerificationCandidateCount} Terra review windows.",
            candidates.Length, verificationCandidates.Count);

        if (verificationCandidates.Count > options.MaximumSceneVerificationRequestsPerJob)
        {
            throw new InvalidOperationException(
                $"Scene verification produced {verificationCandidates.Count} candidates, " +
                $"above the configured limit of {options.MaximumSceneVerificationRequestsPerJob}. " +
                "The job was stopped to prevent unbounded model spending.");
        }

        // Keep each request isolated to one scene, but run independent scenes concurrently.
        var batches = verificationCandidates.Chunk(1).ToArray();
        logger.LogInformation(
            "Terra verification planned: {TerraCandidateCount} sexual events will be sent to Terra.",
            batches.Length);
        var sourceRanges = verificationCandidates.ToDictionary(
            item => item.CandidateKey,
            item => (item.ProposedStartTime, item.ProposedEndTime),
            StringComparer.Ordinal);
        var terraResults = await RunSceneVerifications(
            batches, options.SceneVerificationModel, options.SceneVerificationConcurrency,
            progress => reportProgress?.Invoke(progress * .5), cancellationToken);
        var sourceCandidates = verificationCandidates.ToDictionary(
            item => item.CandidateKey, StringComparer.Ordinal);
        var terraDecisions = terraResults
            .SelectMany(result => result.Payload.Candidates.Select(decision =>
                (TerraIndex: result.Index, Decision: decision)))
            .ToArray();
        var escalationCandidates = terraDecisions
            .Where(item => item.Decision.Accepted || item.Decision.NeedsEscalation)
            .Select(item => sourceCandidates.TryGetValue(
                    item.Decision.CandidateKey, out var source)
                ? new SolEscalationCandidate(item.TerraIndex, source)
                : null)
            .Where(item => item is not null)
            .Select(item => item!)
            .DistinctBy(item => item.Source.CandidateKey)
            .ToArray();

        // Fails rather than truncating. This used to `.Take()` the cap, so scenes past it
        // silently fell back to their Terra decision and the ambiguous ones were dropped
        // outright -- a scan that quietly covered less than it should while reporting
        // success. Its sibling verification cap has always thrown; the two now agree, and a
        // job that hits this needs the cap raised rather than the result trimmed.
        if (escalationCandidates.Length > options.MaximumSceneEscalationRequestsPerJob)
        {
            throw new InvalidOperationException(
                $"Scene escalation produced {escalationCandidates.Length} candidates, above " +
                $"the configured limit of {options.MaximumSceneEscalationRequestsPerJob}. " +
                "The job was stopped rather than scanning part of the audiobook.");
        }

        logger.LogInformation(
            "Sol escalation planned: {SolCandidateCount} confirmed or ambiguous scene candidates " +
            "from {TerraCandidateCount} Terra decisions; concurrency {SolConcurrency}.",
            escalationCandidates.Length, terraDecisions.Length,
            Math.Max(1, options.SceneEscalationConcurrency));

        var solDecisions = await RunSolEscalations(
            escalationCandidates,
            progress => reportProgress?.Invoke(.5 + progress * .5),
            cancellationToken);
        // Grouped rather than keyed directly. Each request carries one candidate, but the
        // schema permits an array, so a model returning two decisions for the same key threw
        // and failed the job at the very last step, after every model call had been paid for.
        // The most confident decision wins, matching how the first pass deduplicates.
        var solByCandidate = solDecisions
            .GroupBy(item => item.CandidateKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.Confidence).First(),
                StringComparer.Ordinal);

        foreach (var (_, terraDecision) in terraDecisions)
        {
            var verification = solByCandidate.GetValueOrDefault(
                terraDecision.CandidateKey, terraDecision);
            if (!verification.Accepted || !verification.DirectSexualActEvidence ||
                !verification.SustainedBeyondKissing || verification.Confidence < .85 ||
                !sourceRanges.TryGetValue(verification.CandidateKey, out var sourceRange))
                continue;
            var start = Math.Clamp(verification.StartTime,
                sourceRange.ProposedStartTime - 30, sourceRange.ProposedEndTime);
            var end = Math.Clamp(verification.EndTime,
                start, sourceRange.ProposedEndTime + 30);
            retained.Add(new ScanEvent(
                Guid.NewGuid(), start, end, mapping.CategoryID, mapping.GroupID,
                mapping.EventID, verification.Confidence,
                Hash($"verified-scene|{start:F1}|{end:F1}|{verification.CandidateKey}"),
                SafeDescriptionForEvent("sexual_complete_scene", verification.SafeDescription)));
        }

        reportProgress?.Invoke(1);

        logger.LogInformation(
            "Sexual-scene verification retained {RetainedCount} of {CandidateCount} candidates " +
            "after {EscalationCount} capped escalation requests.",
            retained.Count(item => item.EventID == mapping.EventID),
            verificationCandidates.Count,
            escalationCandidates.Length);
        return retained;
    }

    private async Task<IReadOnlyList<VerifiedSceneCandidate>> RunSolEscalations(
        IReadOnlyList<SolEscalationCandidate> candidates,
        Action<double>? reportProgress,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            reportProgress?.Invoke(1);
            return [];
        }

        using var gate = new SemaphoreSlim(Math.Max(1, options.SceneEscalationConcurrency));
        var completed = 0;
        var tasks = candidates.Select(async (candidate, index) =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                var batch = new[] { candidate.Source };
                var checkpointPath = SceneVerificationCheckpointPath(
                    batch, SceneEscalationVersion, options.SceneEscalationModel);
                var payload = await LoadSceneVerificationCheckpoint(
                    checkpointPath, cancellationToken);
                if (payload is null)
                {
                    payload = await VerifySceneBatch(
                        batch, options.SceneEscalationModel, cancellationToken);
                    await SaveSceneVerificationCheckpoint(
                        checkpointPath, payload, cancellationToken);
                }

                var finished = Interlocked.Increment(ref completed);
                logger.LogInformation(
                    "Sol escalation progress: {Completed}/{Total} candidate scenes; " +
                    "Terra candidate {TerraCandidateNumber}.",
                    finished, candidates.Count, candidate.TerraIndex + 1);
                reportProgress?.Invoke(finished / (double)candidates.Count);
                return payload.Candidates;
            }
            finally
            {
                gate.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.SelectMany(item => item).ToArray();
    }

    private static IReadOnlyList<SceneVerificationCandidate> CoalesceSceneCandidates(
        IReadOnlyList<SceneVerificationCandidate> candidates)
    {
        const double mergeGapSeconds = 45;
        var ordered = candidates.OrderBy(item => item.ProposedStartTime).ToArray();
        var result = new List<SceneVerificationCandidate>();
        var group = new List<SceneVerificationCandidate>();
        var groupEnd = double.MinValue;

        void Flush()
        {
            if (group.Count == 0) return;
            var first = group[0];
            var last = group[^1];
            var start = group.Min(item => item.ProposedStartTime);
            var end = group.Max(item => item.ProposedEndTime);
            var mergedSegments = group
                .SelectMany(item => item.Segments)
                .DistinctBy(item => (item.StartTime, item.EndTime))
                .OrderBy(item => item.StartTime)
                .ToArray();
            result.Add(new SceneVerificationCandidate(
                Hash($"coalesced-scene|{first.CandidateKey}|{last.CandidateKey}"),
                start, end, mergedSegments));
            group.Clear();
        }

        foreach (var candidate in ordered)
        {
            if (group.Count > 0 && candidate.ProposedStartTime > groupEnd + mergeGapSeconds)
                Flush();
            group.Add(candidate);
            groupEnd = Math.Max(groupEnd, candidate.ProposedEndTime);
        }
        Flush();
        return result;
    }

    private async Task<IReadOnlyList<SceneBatchResult>> RunSceneVerifications(
        IReadOnlyList<SceneVerificationCandidate[]> batches,
        string model,
        int concurrency,
        Action<double>? reportProgress,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var completed = 0;
        var tasks = batches.Select((batch, index) => VerifySceneBatchWithCheckpoint(
            index, batch, model, gate,
            () =>
            {
                var finished = Interlocked.Increment(ref completed);
                logger.LogInformation(
                    "Terra verification progress: {Completed}/{Total} candidate windows.",
                    finished, batches.Count);
                reportProgress?.Invoke(finished / (double)Math.Max(1, batches.Count));
            },
            cancellationToken));
        return await Task.WhenAll(tasks);
    }

    private async Task<SceneBatchResult> VerifySceneBatchWithCheckpoint(
        int index,
        IReadOnlyList<SceneVerificationCandidate> batch,
        string model,
        SemaphoreSlim gate,
        Action completed,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var checkpointPath = SceneVerificationCheckpointPath(batch, model: model);
            var payload = await LoadSceneVerificationCheckpoint(checkpointPath, cancellationToken);
            if (payload is null)
            {
                payload = await VerifySceneBatch(batch, model, cancellationToken);
                await SaveSceneVerificationCheckpoint(checkpointPath, payload, cancellationToken);
            }
            else
            {
                logger.LogInformation(
                    "Reused sexual-scene verification checkpoint {BatchNumber}; no API request was made.",
                    index + 1);
            }
            completed();
            return new SceneBatchResult(index, batch, payload);
        }
        finally { gate.Release(); }
    }

    private async Task<SceneVerificationCandidate[]> PrefilterWithVersion1p1(
        IReadOnlyList<SceneVerificationCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var retainedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var batch in candidates.Chunk(4))
        {
            var checkpoint = await LoadSceneVerificationCheckpoint(
                SceneVerificationCheckpointPath(batch, "1.1"), cancellationToken);
            if (checkpoint is null)
            {
                // A book that never ran 2.4 still receives complete 2.5 verification.
                foreach (var candidate in batch) retainedKeys.Add(candidate.CandidateKey);
                continue;
            }
            foreach (var item in checkpoint.Candidates.Where(item =>
                item.Accepted && item.Confidence >= .75))
            {
                retainedKeys.Add(item.CandidateKey);
            }
        }
        var filtered = candidates.Where(item => retainedKeys.Contains(item.CandidateKey)).ToArray();
        logger.LogInformation(
            "Prior verifier checkpoints reduced {CandidateCount} candidates to {FilteredCount} " +
            "for isolated final verification.", candidates.Count, filtered.Length);
        return filtered;
    }

    private string SceneVerificationCheckpointPath(
        IReadOnlyList<SceneVerificationCandidate> candidates,
        string? version = null,
        string? model = null)
    {
        var material = $"{model ?? options.SceneVerificationModel}|scene-verifier-{version ?? SceneVerificationVersion}|" +
            JsonSerializer.Serialize(candidates);
        return Path.Combine(_checkpointFolder, $"scene-{Hash(material)}.json");
    }

    private static async Task<SceneVerificationPayload?> LoadSceneVerificationCheckpoint(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var input = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<SceneVerificationPayload>(
                input, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task SaveSceneVerificationCheckpoint(
        string path,
        SceneVerificationPayload payload,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        await using (var output = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(
                output, payload, cancellationToken: cancellationToken);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private async Task<SceneVerificationPayload> VerifySceneBatch(
        IReadOnlyList<SceneVerificationCandidate> candidates,
        string model,
        CancellationToken cancellationToken)
    {
        var input = """
Act as a strict final verifier for one audiobook sexual-scene skip range.
Each candidate was produced by a high-recall detector and may be a false positive.
Set directSexualActEvidence=true only when this candidate's own transcript directly supports
an ongoing sexual act. Set sustainedBeyondKissing=true only when that act continues as a
narrative scene beyond attraction, dialogue, kissing, embracing, or nudity alone. A discussion
of past sex is a reference, not an ongoing act. Flirting, suggestive language, attraction,
kissing alone, embraces, nudity alone, sexual jokes or references, profanity, medical
discussion, violence, combat, pain, breathing, groaning, or the word "thrust" in a non-sexual
context must be rejected. Do not infer an act from tone, romance, or physical closeness.
Do not require graphic anatomical vocabulary. In context, physical sexual escalation such as
intimate touching (for example a hand moving onto a thigh), opening or spreading legs, removing
clothing, intimate caressing, or explicit consent/positioning is direct evidence when it is part
of an ongoing sexual encounter. A combination of these cues must not be downgraded merely because
the narration is euphemistic or non-graphic.

accepted may be true only when BOTH evidence booleans are true and confidence is at least
0.85. Otherwise accepted must be false. Set needsEscalation=true only when the candidate is
still a plausible ongoing sexual scene but the evidence, confidence, or exact boundaries are
uncertain and require a stronger final review. Set needsEscalation=false for clear rejections,
isolated innuendo, references, attraction, kissing, or nudity alone. Confirmed accepted scenes
will also receive final review. Confidence must describe the evidence for the sustained act,
not merely for one suggestive word.

For an accepted candidate, refine startTime to the beginning of the sustained sexual activity
or its immediate unmistakable lead-in, and endTime where that activity clearly finishes.
Keep timestamps within the supplied excerpt. Use a neutral, non-graphic but useful description
that distinguishes the scene from a single explicit phrase. Do not return the generic wording
"Complete sexual scene". Examples of acceptable style are "Sustained consensual sexual
activity" or "Sexual activity following romantic dialogue". Do not include graphic details
or quotations. Never name intimate anatomy or describe touching mechanics, positions, squeezing,
or similar physical details. Return one decision for every candidateKey.

Candidates:
""" + JsonSerializer.Serialize(candidates);
        var response = await modelClient.CompleteJson(
            model,
            input,
            "audiochoice_scene_verification",
            SceneVerificationResponseSchema(),
            cancellationToken);
        RecordUsage(model, response);

        return ReadPayload<SceneVerificationPayload>(response.Json, "Scene verification")
            ?? throw new InvalidOperationException("Scene verification returned no structured result.");
    }

    /// <summary>
    /// Records what one model call cost, per model, so a scan's spend is attributable.
    /// </summary>
    /// <remarks>
    /// Logged rather than stored, for now. It answers "which tier is the expensive one" from
    /// a job's own output, which is the question that decides whether moving the high-volume
    /// first pass to a cheaper model is worth anything. A usage count a vendor did not return
    /// is reported as unknown rather than as zero.
    /// </remarks>
    private void RecordUsage(string model, AnalysisModelResponse response)
    {
        logger.LogInformation(
            "Model usage: {Provider} {Model} in={InputTokens} out={OutputTokens}.",
            modelClient.ProviderName,
            model,
            response.InputTokens?.ToString() ?? "unknown",
            response.OutputTokens?.ToString() ?? "unknown");
    }

    /// <summary>
    /// The allowed labels as the prompt states them, wrapped for readability.
    /// </summary>
    /// <remarks>
    /// Built from <see cref="ContentTaxonomy.EnforcedLabels"/> rather than written out again,
    /// so the prompt, the response schema and the taxonomy cannot disagree about what the
    /// model may return.
    /// </remarks>
    internal static readonly string AllowedLabelList = BuildAllowedLabelList();

    private static string BuildAllowedLabelList()
    {
        var builder = new StringBuilder();
        var lineLength = 0;
        for (var index = 0; index < ContentTaxonomy.EnforcedLabels.Count; index += 1)
        {
            var label = ContentTaxonomy.EnforcedLabels[index];
            var last = index == ContentTaxonomy.EnforcedLabels.Count - 1;
            var token = label + (last ? "." : ",");
            if (lineLength > 0 && lineLength + token.Length + 1 > 78)
            {
                builder.Append('\n');
                lineLength = 0;
            }
            else if (lineLength > 0)
            {
                builder.Append(' ');
                lineLength += 1;
            }
            builder.Append(token);
            lineLength += token.Length;
        }
        return builder.ToString();
    }

    internal static string BuildInput(
        IReadOnlyList<TranscriptSegment> segments)
    {
        var transcript = JsonSerializer.Serialize(segments);

        return """
Act as an audiobook content-preference classifier. Identify only events that are explicitly
supported by the supplied transcript.

violence_graphic has one test, and it is a high one: the narration must dwell on the physical
detail of a body being damaged. Flesh being cut, torn or opened; blood flowing or pooling;
bones breaking; organs, entrails or brain matter; a limb or head being severed; a wound
described closely enough that a listener pictures the injury rather than the act. It is the
lingering physical description that qualifies, not the violence itself.

An act of violence stated without that physical detail is NOT violence_graphic. Do not flag: a
punch, a slap, a shove, a slammed door, a stabbing or shooting reported without describing the
wound, a battle or duel, a threat, a character being hurt or killed, a body being found,
bruises, scars, a mention of blood in passing, medical treatment, pain, an injury's aftermath,
grief, or fantasy peril. Most fight scenes are not graphic. If you are weighing whether the
description is detailed enough, it is not: omit it.

Return violence_graphic for perhaps a handful of moments in an entire book, and none at all in
most books. A count in the dozens means the test above is being applied too loosely.

violence_torture holds to the same physical-detail test as violence_graphic. Deliberate,
sustained cruelty whose injuries the narration describes closely: wounds opened, flesh burned or
cut, bones broken, blood. Captivity and beating on their own are not torture for this purpose --
a character tied to a chair and punched is not filtered, however unpleasant the scene is. If the
narration does not dwell on the physical damage, omit it.

violence_children and violence_animals are judged on what happens rather than how it is
described, because who it happens to is the point of those two. Self-harm and suicide are their
own category and are unchanged.

For isolated events, return the narrowest supported timestamps. A short reference is a short
event: if three words carry it, the range should cover those three words and not the sentence
or paragraph around them. Never widen a brief event to be safe -- a wide range on a passing
reference removes narration the listener wanted to hear. Sexual activity is different:
identify the complete narrative scene, including its clear lead-in and the point where sexual
activity ends and the story returns to non-sexual action or conversation. Whenever explicit
sexual activity occurs, ALWAYS also emit one sexual_complete_scene event spanning that whole
scene, even when only part of the scene uses explicit vocabulary. Do not reduce a scene to the
single explicit sentence that made it recognizable.
Physical escalation and intimate positioning are explicit-activity evidence even when euphemistic:
intimate touching such as a hand moving onto a thigh, opening or spreading legs, removing clothing,
intimate caressing, or explicit consent/positioning. When these cues occur together in a continuing
encounter, emit sexual_explicit_activity and sexual_complete_scene across the full supported scene;
do not require graphic anatomical terms.

If a sexual scene was already underway at the first supplied segment, set the scene start to
that first segment's startTime. If it is still underway at the last supplied segment, set its
end to that last segment's endTime. The server analyzes overlapping windows and will join those
partial ranges. A scene can span several minutes. Consensual romance, foreplay, implied acts,
explicit acts, and the immediate aftermath may establish continuity even when no explicit word
appears in every segment. Do not extend a scene across a clear topic, location, time, or chapter
change.
Allowed labels:
""" + AllowedLabelList + """
For safeDescription, write a neutral, discreet, non-graphic summary of at most 80 characters.
It must still tell a parent why they might choose to skip: name the high-level situation, not
the mechanics. For example: "A character removes clothing and is described without clothing",
"Characters are in bed together during an intimate encounter", "Suggestive dialogue includes
an invitation to bed", or "A serious violent encounter causes an injury". Never name intimate
anatomy or describe touching, squeezing, positions, or mechanics. Do not use vague filler such
as "related sexual content", "continuous passage", "sexual activity described", or
"intimate positioning is described".
Never describe gore, wounds, removed body parts, or the method used for self-harm or suicide.
Use clean wording such as "Graphic violence is described", "Torture is described",
"Self-harm is depicted", or "Suicidal thoughts are described". The description itself must
not expose a listener to the unwanted graphic or explicit material they are trying to avoid.
Do not quote transcript text. For profanity labels only, return the exact single profane word
in profanityWord so the server can censor and count it; otherwise return null.
For profanity, emit one event for every occurrence so playback can skip each timestamp; the
app will group all occurrences of the same word under one switch. For longer sexual, violent, substance,
or self-harm scenes, emit one event spanning the complete supported scene rather than one
event per sentence. Do not invent content or identifiers. Omit events below 0.55 confidence.

Transcript segments:
""" + transcript;
    }

    /// <summary>
    /// The shape a first-pass answer must take. Vendor-neutral JSON Schema: the transport
    /// decides how to impose it, whether as a response format or as a tool definition.
    /// </summary>
    private static JsonObject AnalysisResponseSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("events"),
        ["properties"] = new JsonObject
        {
            ["events"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray(
                        "label", "startTime", "endTime", "confidence",
                        "safeDescription", "profanityWord"),
                    ["properties"] = new JsonObject
                    {
                        ["label"] = new JsonObject
                        {
                            ["type"] = "string",
                            // Derived from the taxonomy so the schema cannot
                            // permit a label the taxonomy would then discard.
                            ["enum"] = new JsonArray(ContentTaxonomy.EnforcedLabels
                                .Select(label => (JsonNode)JsonValue.Create(label)!)
                                .ToArray())
                        },
                        ["startTime"] = new JsonObject { ["type"] = "number" },
                        ["endTime"] = new JsonObject { ["type"] = "number" },
                        ["confidence"] = new JsonObject
                        {
                            ["type"] = "number",
                            ["minimum"] = 0,
                            ["maximum"] = 1
                        },
                        ["safeDescription"] = new JsonObject { ["type"] = "string", ["maxLength"] = 80 },
                        ["profanityWord"] = new JsonObject
                        {
                            ["type"] = new JsonArray("string", "null"),
                            ["maxLength"] = 80
                        }
                    }
                }
            }
        }
    };

    /// <summary>The shape a verification or escalation answer must take.</summary>
    private static JsonObject SceneVerificationResponseSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("candidates"),
        ["properties"] = new JsonObject
        {
            ["candidates"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray(
                        "candidateKey", "accepted", "needsEscalation", "directSexualActEvidence",
                        "sustainedBeyondKissing", "startTime", "endTime",
                        "confidence", "safeDescription"),
                    ["properties"] = new JsonObject
                    {
                        ["candidateKey"] = new JsonObject { ["type"] = "string" },
                        ["accepted"] = new JsonObject { ["type"] = "boolean" },
                        ["needsEscalation"] = new JsonObject { ["type"] = "boolean" },
                        ["directSexualActEvidence"] = new JsonObject { ["type"] = "boolean" },
                        ["sustainedBeyondKissing"] = new JsonObject { ["type"] = "boolean" },
                        ["startTime"] = new JsonObject { ["type"] = "number" },
                        ["endTime"] = new JsonObject { ["type"] = "number" },
                        ["confidence"] = new JsonObject
                        {
                            ["type"] = "number",
                            ["minimum"] = 0,
                            ["maximum"] = 1
                        },
                        ["safeDescription"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["maxLength"] = 80
                        }
                    }
                }
            }
        }
    };

    private sealed record AnalysisPayload(
        [property: JsonPropertyName("events")]
        IReadOnlyList<ClassifiedEvent> Events);

    private sealed record ContentBatchResult(
        int Index,
        IReadOnlyList<TranscriptSegment> Batch,
        AnalysisPayload Payload);

    private sealed record ClassifiedEvent(
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("startTime")] double StartTime,
        [property: JsonPropertyName("endTime")] double EndTime,
        [property: JsonPropertyName("confidence")] double Confidence,
        [property: JsonPropertyName("safeDescription")] string SafeDescription,
        [property: JsonPropertyName("profanityWord")] string? ProfanityWord);

    private sealed record SceneVerificationCandidate(
        [property: JsonPropertyName("candidateKey")] string CandidateKey,
        [property: JsonPropertyName("proposedStartTime")] double ProposedStartTime,
        [property: JsonPropertyName("proposedEndTime")] double ProposedEndTime,
        [property: JsonPropertyName("segments")] IReadOnlyList<TranscriptSegment> Segments);

    private sealed record SceneVerificationPayload(
        [property: JsonPropertyName("candidates")]
        IReadOnlyList<VerifiedSceneCandidate> Candidates);

    private sealed record SceneBatchResult(
        int Index,
        IReadOnlyList<SceneVerificationCandidate> Batch,
        SceneVerificationPayload Payload);

    private sealed record SolEscalationCandidate(
        int TerraIndex,
        SceneVerificationCandidate Source);

    private sealed record VerifiedSceneCandidate(
        [property: JsonPropertyName("candidateKey")] string CandidateKey,
        [property: JsonPropertyName("accepted")] bool Accepted,
        [property: JsonPropertyName("needsEscalation")] bool NeedsEscalation,
        [property: JsonPropertyName("directSexualActEvidence")] bool DirectSexualActEvidence,
        [property: JsonPropertyName("sustainedBeyondKissing")] bool SustainedBeyondKissing,
        [property: JsonPropertyName("startTime")] double StartTime,
        [property: JsonPropertyName("endTime")] double EndTime,
        [property: JsonPropertyName("confidence")] double Confidence,
        [property: JsonPropertyName("safeDescription")] string SafeDescription);

    private static string SafeDescription(string? value)
    {
        var cleaned = string.Join(' ', (value ?? "Content event detected").Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return cleaned[..Math.Min(cleaned.Length, 80)];
    }

    private static string StableEventKey(
        TaxonomyMapping mapping,
        double start,
        double end,
        string description,
        string? discriminator = null) =>
        Hash($"event|{mapping.EventID:N}|{Math.Round(start, 1):F1}|{Math.Round(end, 1):F1}|" +
            $"{SafeDescription(description).ToLowerInvariant()}|{discriminator}");

    private static string? AggregateKey(string? word) => string.IsNullOrWhiteSpace(word)
        ? null : Hash($"word|{word.Trim().ToLowerInvariant()}");

    private static string? CensorWord(string? word)
    {
        if (string.IsNullOrWhiteSpace(word)) return null;
        var value = word.Trim();
        if (value.Length <= 2) return new string('*', value.Length);
        return value[0] + new string('*', value.Length - 2) + value[^1];
    }

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
