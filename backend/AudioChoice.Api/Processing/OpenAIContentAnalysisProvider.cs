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
    : IContentAnalysisProvider
{
    // Bump this whenever the baseline classification policy changes so cached batch
    // answers cannot silently reintroduce events produced under an older policy. Bumped for
    // this pipeline overhaul: Luna no longer proposes profanity labels, batches are
    // paragraph/sentence-aligned with 15% (not 50%) overlap, Terra's context window is
    // sentence-bounded rather than a flat ±20s, its entry gate now excludes lone weak
    // singletons, Sol's boundary is quote-confirmed-and-word-snapped rather than clamped to
    // ±30s, Sol is only invoked for ambiguous/low-confidence Terra results, and scene merging
    // is sentence-boundary-based rather than a flat 45s gap.
    //
    // Bumped again for sexual_violence: Luna's prompt now defines it as mutually exclusive
    // with the consensual scene ladder, and Terra/Sol's own prompts and schema gained a
    // second, consent-specific verification lane (VerifySceneBatch, ResolveSceneOutcome) that
    // did not exist under the prior version -- a cached answer from before this change never
    // considered whether the passage was non-consensual at all.
    private const string BaseAnalysisPromptVersion = "5.1-sexual-violence";
    // Bumped for the keyword safety net's lane isolation fix: a candidate whose window
    // happens to match a checkpoint cached under the prior version may have been built
    // before a safety-net seed's own lane existed, when it could still get coalesced into a
    // real Luna candidate's review window and rejected as a diluted, wider passage. Reusing
    // that cached rejection here would silently keep serving the exact bug this fix exists
    // to close, even after the code itself is corrected.
    //
    // Bumped again: a keyword-safety-net candidate's Terra rejection now escalates to Sol
    // for a second opinion (see NeedsSolReview's remarks), which a cached Terra-only
    // checkpoint from before this change never had a chance to do.
    private const string SceneVerificationVersion =
        "5.2-sexual-safety-net-sol-second-opinion";
    private const string SceneEscalationVersion =
        "5.2-sexual-safety-net-sol-second-opinion";
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
                lambdaEvents, segments, new HashSet<Guid>(),
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
        IReadOnlyList<(int StartIndex, int EndExclusive)> batchRanges = options.LocalCandidateFunnelEnabled
            ? DeterministicContentDetector.CandidateWindows(segments)
            : ComputeAnalysisBatchRanges(segments, batchSize);

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
                    batchStart, batchEnd, quote: item.Quote,
                    contextSegments: batch)) admitted += 1;
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
        var (supplementedEvents, keywordSafetyNetSeedIDs) =
            AddUncoveredSexualCandidates(uniqueEvents, segments);
        uniqueEvents = supplementedEvents.ToArray();
        var verifiedEvents = await VerifyCompleteSexualScenes(
            uniqueEvents, segments, keywordSafetyNetSeedIDs,
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
    /// The overlap between consecutive batches, as a fraction of the batch size.
    /// </summary>
    /// <remarks>
    /// Reduced from 50%. Every segment inside a 50% overlap was classified twice, at twice
    /// the cost, for coverage a paragraph-aligned boundary (see
    /// <see cref="ComputeAnalysisBatchRanges"/>) already protects more directly: a scene is
    /// far less likely to be cut in half by a batch boundary that already falls on a
    /// paragraph break than by one that falls at a fixed segment count. 15% keeps a smaller
    /// safety margin for the rarer case a scene still spans a chosen break, without paying to
    /// reclassify most of the book a second time.
    /// </remarks>
    private const double AnalysisBatchOverlapFraction = .15;

    /// <summary>
    /// Splits the transcript into Luna's review batches, preferring a paragraph or chapter
    /// break near each target boundary over a fixed segment count.
    /// </summary>
    /// <remarks>
    /// A fixed-size window can end mid-scene regardless of overlap; a boundary chosen at a
    /// natural break in the narrative is far less likely to. This looks for a segment whose
    /// own text ends a sentence, and beyond that a blank/short segment (the closest a
    /// transcript's own text comes to a paragraph or scene break, since spoken narration
    /// carries no chapter markers of its own) within a search window around the fixed target,
    /// falling back to the fixed boundary itself when nothing better is found nearby -- so a
    /// transcript with no punctuation at all still batches, just without the benefit.
    /// </remarks>
    internal static IReadOnlyList<(int StartIndex, int EndExclusive)> ComputeAnalysisBatchRanges(
        IReadOnlyList<TranscriptSegment> segments,
        int batchSize)
    {
        if (segments.Count == 0) return [];

        var overlap = Math.Max(0, (int)Math.Round(batchSize * AnalysisBatchOverlapFraction));
        var step = Math.Max(1, batchSize - overlap);
        // How far from the fixed target boundary a natural break may be adopted instead.
        var searchRadius = Math.Max(1, step / 4);

        var ranges = new List<(int StartIndex, int EndExclusive)>();
        var start = 0;
        while (start < segments.Count)
        {
            var target = Math.Min(segments.Count, start + batchSize);
            var end = target >= segments.Count
                ? segments.Count
                : NearestNaturalBreak(segments, target, searchRadius, start);
            ranges.Add((start, end));
            if (end >= segments.Count) break;
            start = Math.Max(start + 1, end - overlap);
        }
        return ranges;
    }

    /// <summary>
    /// The segment boundary nearest <paramref name="target"/>, within
    /// <paramref name="searchRadius"/> segments, that ends a sentence -- the closest a
    /// spoken transcript's own text comes to a paragraph break. Falls back to the fixed
    /// target itself when no such boundary exists nearby.
    /// </summary>
    private static int NearestNaturalBreak(
        IReadOnlyList<TranscriptSegment> segments, int target, int searchRadius, int rangeStart)
    {
        var low = Math.Max(rangeStart + 1, target - searchRadius);
        var high = Math.Min(segments.Count, target + searchRadius);
        for (var offset = 0; offset <= searchRadius; offset += 1)
        {
            var after = target + offset;
            if (after >= low && after <= high && after < segments.Count &&
                TranscriptSentenceBoundaries.EndsWithSentenceEnd(segments[after].Text))
            {
                return after + 1;
            }
            var before = target - offset;
            if (before >= low && before <= high && before > rangeStart && before - 1 >= 0 &&
                TranscriptSentenceBoundaries.EndsWithSentenceEnd(segments[before - 1].Text))
            {
                return before;
            }
        }
        return Math.Min(segments.Count, target);
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

    /// <summary>
    /// The furthest a Terra context window may expand on each side, in seconds, once it has
    /// reached a sentence boundary.
    /// </summary>
    /// <remarks>
    /// A ceiling on the expansion itself, not a target: most windows stop earlier, right at
    /// the nearest sentence break, and only a passage with unusually long sentences (or none
    /// at all, in an unpunctuated transcript) would ever reach this. Exists so a chapter
    /// cannot be read to Terra whole just because no sentence terminator appeared for a long
    /// stretch.
    /// </remarks>
    private const double MaximumContextExpansionSeconds = 60;

    /// <summary>
    /// The transcript segments Terra is shown for one candidate: the segments the candidate
    /// itself spans, expanded outward to the nearest sentence boundary on each side.
    /// </summary>
    /// <remarks>
    /// Replaces a flat ±20-second window. Terra genuinely needs surrounding narrative to
    /// judge context -- that requirement does not go away -- but a flat number of seconds
    /// could start or end its reading mid-sentence, handing the model half a thought on
    /// either edge. Expanding to where a sentence actually ends or begins gives it the same
    /// kind of context on cleaner terms, at no extra model cost: this is read purely from the
    /// transcript's own punctuation, the same way <see cref="SceneEventPostProcessor"/>'s
    /// merge decision is. This window is context for the model call only and must never
    /// become the stored event's boundary -- that is decided separately, by word-snapping
    /// Sol's (or Terra's own) refined range against the transcript's actual words.
    /// </remarks>
    internal static IReadOnlyList<TranscriptSegment> SentenceBoundedContext(
        double start,
        double end,
        IReadOnlyList<TranscriptSegment> segments)
    {
        var ordered = segments.OrderBy(segment => segment.StartTime).ToArray();
        var coreIndices = ordered
            .Select((segment, index) => (segment, index))
            .Where(item => item.segment.EndTime >= start && item.segment.StartTime <= end)
            .Select(item => item.index)
            .ToArray();
        if (coreIndices.Length == 0) return [];

        var lowIndex = coreIndices.Min();
        var highIndex = coreIndices.Max();

        var firstIndex = lowIndex;
        while (firstIndex > 0)
        {
            var candidate = ordered[firstIndex - 1];
            // Measured against the candidate's own original boundary, not the enclosing
            // segment's -- the cap is a promise about how far past the actual event this
            // window may reach, not about the segment grid it happens to be sliced into.
            if (start - candidate.StartTime > MaximumContextExpansionSeconds) break;
            firstIndex -= 1;
            // This segment itself closes a sentence, so the window now begins at the start
            // of a new one -- a natural place to stop expanding backward.
            if (TranscriptSentenceBoundaries.EndsWithSentenceEnd(candidate.Text)) break;
        }

        var lastIndex = highIndex;
        while (lastIndex < ordered.Length - 1)
        {
            var candidate = ordered[lastIndex + 1];
            if (candidate.EndTime - end > MaximumContextExpansionSeconds) break;
            lastIndex += 1;
            // This newly included segment itself closes a sentence, so the window now ends
            // at a natural break rather than mid-thought.
            if (TranscriptSentenceBoundaries.EndsWithSentenceEnd(candidate.Text)) break;
        }

        return ordered[firstIndex..(lastIndex + 1)];
    }

    /// <summary>
    /// How far a located quote may sit from the model's own claimed range and still confirm
    /// it, rather than the claim being treated as unrelated to any text that actually exists.
    /// </summary>
    /// <remarks>
    /// Matches the slack already trusted elsewhere in this file for a model-refined boundary
    /// (Sol's escalation clamp is the same 30 seconds), rather than inventing a second number
    /// for the same kind of judgement call.
    /// </remarks>
    private const double QuoteProximitySeconds = 30;

    /// <summary>
    /// The claimed width, in seconds, below which a located quote's own tight span replaces
    /// the model's numbers outright rather than merely confirming them.
    /// </summary>
    /// <remarks>
    /// Below this a claimed range is an isolated phrase or short passage, where the located
    /// quote's own word-level timing is strictly better evidence than the model's guess at
    /// seconds. At or above it the model was asked to report the complete span of a sustained
    /// scene, where the quote is deliberately only one supporting sentence inside a longer
    /// arc and must not shrink the boundary down to it.
    /// </remarks>
    private const double ShortEventMaximumSeconds = 15;

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
        string? stableSuffix = null,
        string? quote = null,
        IReadOnlyList<TranscriptSegment>? contextSegments = null,
        bool isCharacterOffsets = false)
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

        // contextSegments is only supplied for a model-proposed event -- deterministic
        // profanity and the Lambda candidate-window overview already carry exact or
        // deliberately approximate timing of their own and skip this. For everything else,
        // the model's startTime/endTime are a number it invented and are trusted here only
        // after being confirmed against words that actually appear in the transcript; see
        // TranscriptWordLocator's remarks for why a model-driven category needed the same
        // protection profanity already had.
        if (contextSegments is not null)
        {
            var anchored = AnchorToTranscript(
                startTime, endTime, quote, contextSegments, isCharacterOffsets);
            if (anchored is null)
            {
                logger.LogInformation(
                    "Discarded a proposed {Label} event at {Start:F1}-{End:F1}: its quote " +
                    "could not be located in the supplied transcript.",
                    label, startTime, endTime);
                return false;
            }
            (startTime, endTime) = anchored.Value;
        }

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

    /// <summary>
    /// Confirms a model-proposed event's claimed range against the transcript's own words,
    /// and narrows it to exact word timing when the claim was for a short, isolated phrase.
    /// </summary>
    /// <remarks>
    /// Returns null -- rejecting the event outright -- when the quote cannot be found
    /// anywhere in the supplied segments at all, or when it is found nowhere near the
    /// claimed range. Both are the same failure this exists to catch: a claim with no real
    /// text behind it. This is deliberately stricter than <see cref="DeterministicContentDetector"/>'s
    /// own word matching, which always has a regex-confirmed word somewhere in the segment
    /// and can safely fall back to that segment's bounds. A model's claim carries no such
    /// independent confirmation that the words it describes exist at all, so there is nothing
    /// safe to fall back to.
    /// </remarks>
    private static (double Start, double End)? AnchorToTranscript(
        double claimedStart,
        double claimedEnd,
        string? quote,
        IReadOnlyList<TranscriptSegment> contextSegments,
        bool isCharacterOffsets)
    {
        if (string.IsNullOrWhiteSpace(quote)) return null;

        // The two coordinate spaces this method is ever called in need different matches
        // entirely, and the caller states which one it is rather than this method guessing
        // from whether a segment happens to carry Words: an audio transcript stored before
        // word timings existed also has none, and treating that absence as "this must be a
        // book" would add a character index to a time in seconds and call it a timestamp.
        var located = isCharacterOffsets
            ? TranscriptWordLocator.FindQuotedSubstring(contextSegments, quote)
            : TranscriptWordLocator.FindPhraseInSegments(contextSegments, quote);
        if (located is null) return null;

        // The claim and the located text disagree about where in the book this even is.
        // Neither number is trustworthy at that point, and there is no third source to
        // arbitrate between them.
        if (located.EndTime < claimedStart - QuoteProximitySeconds ||
            located.StartTime > claimedEnd + QuoteProximitySeconds)
        {
            return null;
        }

        // A short, isolated claim: the located quote is not merely supporting evidence for a
        // wider range, it is essentially the whole event, so its own word-level timing is
        // strictly more precise than the model's estimate of the seconds around it.
        if (claimedEnd - claimedStart <= ShortEventMaximumSeconds)
        {
            return (located.StartTime, Math.Max(located.EndTime, located.StartTime));
        }

        // A claimed scene or sustained passage: the quote is one confirmed sentence inside a
        // longer arc the model was asked to report in full, per the prompt's own instruction
        // that its quote need not span the whole scene. Proximity above is what stands in for
        // verifying the rest of the claimed span, since nothing else here can.
        return (claimedStart, claimedEnd);
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
            "sexual_violence" => "Sexual violence is described",
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
        catch (JsonException) when (Flatten(json) is string flattened)
        {
            // Nova returns a list as a string containing the list:
            //   {"candidates":["[{\"candidateKey\": ...}]"]}
            // Every field lookup then fails at a byte offset inside text that reads like the
            // right answer. Repaired here rather than against the schema, because the schema
            // walk that was supposed to catch it did not and the shape is recognisable without
            // one: a value that is a JSON array encoded as a string.
            logger.LogInformation(
                "{What} returned a list encoded as a string; unwrapped it.", what);
            return JsonSerializer.Deserialize<T>(flattened, LenientJson);
        }
        catch (JsonException error)
        {
            logger.LogError(
                "{What} returned JSON that could not be read: {Message}. Payload: {Payload}",
                what, error.Message, json.Length > 900 ? json[..900] : json);
            throw;
        }
    }

    /// <summary>
    /// Rewrites values that are JSON arrays encoded as strings, or returns null if there are none.
    /// </summary>
    /// <remarks>
    /// Only a string that parses cleanly as an array is unwrapped, and only when doing so changes
    /// something. Anything less certain is left alone: reinterpreting a reply that cannot be read
    /// confidently would be inventing an answer, and these replies decide what is removed from
    /// somebody's audiobook.
    /// </remarks>
    private static string? Flatten(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException) { return null; }
        if (root is not JsonObject obj) return null;

        var changed = false;
        foreach (var property in obj.ToArray())
        {
            var candidate = property.Value switch
            {
                JsonValue single when single.TryGetValue(out string? text) => text,
                JsonArray array when array.Count == 1 && array[0] is JsonValue inner &&
                    inner.TryGetValue(out string? nested) => nested,
                _ => null
            };
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            JsonNode? parsed;
            try { parsed = JsonNode.Parse(candidate); }
            catch (JsonException) { continue; }
            if (parsed is not JsonArray) continue;
            obj[property.Key] = parsed;
            changed = true;
        }
        return changed ? obj.ToJsonString() : null;
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
                options.EffectiveViolenceVerificationModel, input,
                "audiochoice_violence_verification", schema, cancellationToken);
            RecordUsage(options.EffectiveViolenceVerificationModel, response);
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

    /// <summary>
    /// A safety net for Luna's own recall on sexual content: runs the same high-recall
    /// keyword windows the Lambda-first pass already relies on
    /// (<see cref="DeterministicContentDetector.CandidateWindows"/>) across the whole
    /// transcript, and seeds an unconfirmed <c>sexual_complete_scene</c> candidate for any
    /// window Luna did not already cover with a sexual-content event of its own.
    /// </summary>
    /// <remarks>
    /// Luna reads full paragraph-aligned batches and is free to use judgement the keyword
    /// scan cannot -- that is why its output is trusted as-is everywhere else in this file.
    /// But a judgement call can miss a passage that builds gradually rather than opening
    /// with unambiguous vocabulary. The keyword scan is wrong constantly on its own -- that
    /// is why <see cref="DeterministicContentDetector"/>'s doc comment warns its cues are
    /// "guesses, not findings" -- so a seed this method adds is deliberately never a final
    /// event a listener could see on its own. Labeled <c>sexual_complete_scene</c> rather
    /// than a weaker label specifically so <see cref="VerifyCompleteSexualScenes"/>'s own
    /// <c>retained</c> filter strips the raw seed unconditionally, the same way it already
    /// strips every complete-scene claim pending Terra's decision: only a label Terra/Sol
    /// goes on to confirm at their normal 0.85-confidence bar ever reaches a listener. A
    /// seed Terra rejects simply disappears, exactly like a real rejected candidate.
    ///
    /// Built directly rather than through <see cref="AddEvent"/> because that method's
    /// confidence floor (<see cref="EffectiveMinimumConfidence"/>, 0.55 outside Lambda-first
    /// mode) exists to gate what a listener sees, and a seed is not that -- gating it there
    /// would silently drop every seed this method exists to create, the same as if Luna's
    /// own first pass had never proposed it at all.
    ///
    /// Skips a window Luna already has a nearby sexual-content event for, so a book Luna
    /// covered thoroughly pays no extra Terra requests for this pass -- only a genuine gap
    /// in Luna's own coverage produces a new candidate.
    ///
    /// Returns the seeded events' own IDs alongside the supplemented list so
    /// <see cref="VerifyCompleteSexualScenes"/> can give them their own Terra-review lane,
    /// distinct from a real Luna-proposed candidate. Coalescing a seed together with a real,
    /// tightly-bounded candidate nearby was diluting a scene Terra had confirmed cleanly on
    /// its own into a wider, noisier passage Terra then rejected outright -- actively hiding
    /// the very scene this method exists to catch, not merely failing to add a new one.
    /// </remarks>
    private (IReadOnlyList<ScanEvent> Events, IReadOnlySet<Guid> SeedIDs)
        AddUncoveredSexualCandidates(
            IReadOnlyList<ScanEvent> events,
            IReadOnlyList<TranscriptSegment> segments)
    {
        var seedIDs = new HashSet<Guid>();
        var completeSceneMapping = ContentTaxonomy.Mappings["sexual_complete_scene"];
        var sexualEventIDs = new[]
        {
            "sexual_suggestive_dialogue", "sexual_references", "sexual_nudity",
            "sexual_implied_activity", "sexual_explicit_activity", "sexual_complete_scene",
            "sexual_violence"
        }.Select(label => ContentTaxonomy.Mappings[label].EventID).ToHashSet();
        var lunaSexualRanges = events
            .Where(item => sexualEventIDs.Contains(item.EventID))
            .Select(item => (item.StartTime, item.EndTime))
            .OrderBy(item => item.StartTime)
            .ToArray();

        var keywordWindows = DeterministicContentDetector.CandidateWindows(segments);
        if (keywordWindows.Count == 0) return (events, seedIDs);

        var supplemented = new List<ScanEvent>(events);
        var added = 0;
        for (var index = 0; index < keywordWindows.Count; index += 1)
        {
            var range = keywordWindows[index];
            var window = segments.Skip(range.StartIndex)
                .Take(range.EndExclusive - range.StartIndex).ToArray();
            if (window.Length == 0) continue;
            var windowStart = window[0].StartTime;
            var windowEnd = window[^1].EndTime;

            // A window Luna already has a sexual-content event anywhere near is coverage,
            // not a gap -- the same proximity margin AnchorToTranscript already uses for
            // "this claim and this quote are describing the same moment."
            var alreadyCovered = lunaSexualRanges.Any(covered =>
                covered.EndTime >= windowStart - QuoteProximitySeconds &&
                covered.StartTime <= windowEnd + QuoteProximitySeconds);
            if (alreadyCovered) continue;

            var description = "Sexual references or suggestive dialogue detected";
            var seedID = Guid.NewGuid();
            supplemented.Add(new ScanEvent(
                seedID, windowStart, windowEnd,
                completeSceneMapping.CategoryID, completeSceneMapping.GroupID,
                completeSceneMapping.EventID, .35,
                StableEventKey(
                    completeSceneMapping, windowStart, windowEnd, description,
                    $"keyword-safety-net-{index}"),
                description, null, null));
            seedIDs.Add(seedID);
            added += 1;
        }

        if (added > 0)
        {
            logger.LogInformation(
                "Keyword safety net seeded {AddedCount} unconfirmed sexual-content " +
                "candidate(s) for Terra review, out of {WindowCount} high-recall windows " +
                "Luna's own pass had not already covered.",
                added, keywordWindows.Count);
        }
        return (supplemented, seedIDs);
    }

    private async Task<IReadOnlyList<ScanEvent>> VerifyCompleteSexualScenes(
        IReadOnlyList<ScanEvent> events,
        IReadOnlyList<TranscriptSegment> segments,
        IReadOnlySet<Guid> keywordSafetyNetSeedIDs,
        Action<double>? reportProgress,
        CancellationToken cancellationToken)
    {
        var completeSceneMapping = ContentTaxonomy.Mappings["sexual_complete_scene"];
        var sexualViolenceMapping = ContentTaxonomy.Mappings["sexual_violence"];
        var sexualEventIDs = new[]
        {
            "sexual_suggestive_dialogue", "sexual_references", "sexual_nudity",
            "sexual_implied_activity", "sexual_explicit_activity", "sexual_complete_scene",
            "sexual_violence"
        }.Select(label => ContentTaxonomy.Mappings[label].EventID).ToHashSet();
        var sexualCandidateEvents = events
            .Where(item => sexualEventIDs.Contains(item.EventID))
            .OrderBy(item => item.StartTime)
            .ToArray();
        var terraEntryEvents = ExcludeLoneWeakSingletons(sexualCandidateEvents, segments);
        if (terraEntryEvents.Count != sexualCandidateEvents.Length)
        {
            logger.LogInformation(
                "Terra entry gate excluded {ExcludedCount} of {TotalCount} sexual candidates " +
                "as isolated weak references not part of a scene or a dense cluster.",
                sexualCandidateEvents.Length - terraEntryEvents.Count, sexualCandidateEvents.Length);
        }
        var candidates = terraEntryEvents
            .Select(item => new SceneVerificationCandidate(
                item.StableKey,
                item.StartTime,
                item.EndTime,
                SentenceBoundedContext(item.StartTime, item.EndTime, segments),
                // Which lane Luna's first pass placed this event in, not a final decision --
                // Terra/Sol's own nonconsensualEvidence field decides the finalized label
                // below. This only keeps a real assault scene's supporting context separate
                // from an unrelated nearby consensual scene when the two are coalesced for
                // review, so one request is never asked to judge both at once.
                //
                // A keyword-safety-net seed (see AddUncoveredSexualCandidates) gets a third,
                // equally isolated lane for the same reason: coalescing it together with a
                // real Luna-proposed candidate nearby was diluting a scene Terra had already
                // confirmed cleanly on its own into a wider, noisier passage Terra then
                // rejected outright.
                item.EventID == sexualViolenceMapping.EventID
                    ? "sexual_violence"
                    : keywordSafetyNetSeedIDs.Contains(item.Id)
                        ? "sexual_keyword_safety_net"
                        : "sexual_complete_scene"))
            .ToArray();

        var retained = events
            .Where(item => item.EventID != completeSceneMapping.EventID &&
                item.EventID != sexualViolenceMapping.EventID)
            .ToList();
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
        // Only what is genuinely ambiguous or borderline reaches Sol: Terra's own
        // needsEscalation flag, an accepted scene whose confidence fell short of
        // SolEscalationConfidenceThreshold, or a rejection of a keyword-safety-net candidate
        // (see NeedsSolReview's remarks for why that last case gets a second opinion rather
        // than being final on Terra's single call). A confident accept is not re-paid-for at
        // Sol -- that does not mean it goes unconfirmed, see NeedsSolReview's remarks.
        var escalationCandidates = terraDecisions
            .Where(item => sourceCandidates.TryGetValue(
                item.Decision.CandidateKey, out var candidateSource) &&
                NeedsSolReview(
                    item.Decision, candidateSource.FirstPassLane == "sexual_keyword_safety_net"))
            .Select(item => sourceCandidates.TryGetValue(
                    item.Decision.CandidateKey, out var source)
                ? new SolEscalationCandidate(item.TerraIndex, source)
                : null)
            .Where(item => item is not null)
            .Select(item => item!)
            .DistinctBy(item => item.Source.CandidateKey)
            .ToArray();
        var escalatedKeys = escalationCandidates
            .Select(item => item.Source.CandidateKey)
            .ToHashSet(StringComparer.Ordinal);

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

        var confidentAcceptsSkippingSol = terraDecisions
            .Count(item => item.Decision.Accepted && !escalatedKeys.Contains(item.Decision.CandidateKey));
        logger.LogInformation(
            "Sol escalation planned: {SolCandidateCount} ambiguous or borderline scene " +
            "candidates from {TerraCandidateCount} Terra decisions will reach Sol; " +
            "{SkippedCount} confident Terra accept(s) finalize without Sol; concurrency " +
            "{SolConcurrency}.",
            escalationCandidates.Length, terraDecisions.Length, confidentAcceptsSkippingSol,
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

        var rejectedForUnconfirmedQuote = 0;
        foreach (var (_, terraDecision) in terraDecisions)
        {
            var verification = solByCandidate.GetValueOrDefault(
                terraDecision.CandidateKey, terraDecision);
            if (!sourceRanges.TryGetValue(verification.CandidateKey, out _) ||
                !sourceCandidates.TryGetValue(verification.CandidateKey, out var sourceCandidate))
                continue;

            var outcome = ResolveSceneOutcome(sourceCandidate.FirstPassLane, verification);
            if (outcome is null) continue;
            var (label, mapping) = outcome.Value;

            // The refined start is a model-invented number, one call further from the
            // transcript than the first pass's own proposal, and it is the only thing here
            // with independent confirmation available: the verifier's quote. No flat ±30s
            // clamp any more -- a claim whose quote cannot be found in the transcript at all
            // is rejected outright rather than falling back to some other range, because a
            // finalized scene boundary this pipeline no longer trusts by proximity alone must
            // not reach a listener as if it had been confirmed.
            var confirmed = TryConfirmSceneBoundary(
                verification.Quote, verification.EndTime, sourceCandidate.Segments);
            if (confirmed is null)
            {
                rejectedForUnconfirmedQuote += 1;
                logger.LogInformation(
                    "Rejected Sol/Terra's accepted {Label} for {CandidateKey}: its boundary " +
                    "could not be confirmed against the transcript's own words.",
                    label, verification.CandidateKey);
                continue;
            }

            var (start, end) = confirmed.Value;

            retained.Add(new ScanEvent(
                Guid.NewGuid(), start, end, mapping.CategoryID, mapping.GroupID,
                mapping.EventID, verification.Confidence,
                Hash($"verified-scene|{mapping.EventID:N}|{start:F1}|{end:F1}|{verification.CandidateKey}"),
                SafeDescriptionForEvent(label, verification.SafeDescription)));
        }
        if (rejectedForUnconfirmedQuote > 0)
        {
            logger.LogInformation(
                "Rejected {RejectedCount} accepted scene candidate(s) whose boundary could " +
                "not be confirmed against the transcript's own words.",
                rejectedForUnconfirmedQuote);
        }

        reportProgress?.Invoke(1);

        logger.LogInformation(
            "Sexual-scene verification retained {RetainedCount} of {CandidateCount} candidates " +
            "after {EscalationCount} capped escalation requests.",
            retained.Count(item => item.EventID == completeSceneMapping.EventID ||
                item.EventID == sexualViolenceMapping.EventID),
            verificationCandidates.Count,
            escalationCandidates.Length);
        return retained;
    }

    /// <summary>The weak, non-committal sexual-content labels a lone mention of should not reach Terra.</summary>
    private static readonly HashSet<Guid> WeakSexualEventIDs =
        new[] { "sexual_suggestive_dialogue", "sexual_references" }
            .Select(label => ContentTaxonomy.Mappings[label].EventID)
            .ToHashSet();

    /// <summary>
    /// Excludes a lone weak sexual-content mention from reaching Terra at all, unless it is
    /// part of a dense cluster of such mentions or co-occurs with a stronger label.
    /// </summary>
    /// <remarks>
    /// A single isolated <c>suggestive_dialogue</c> or <c>sexual_references</c> event -- a
    /// passing flirtation, one crude joke -- is not a scene candidate and never becomes one no
    /// matter what Terra says about it; spending a Terra call on it is pure waste. What does
    /// still deserve review: three or more weak mentions clustered together with no complete
    /// sentence of ordinary narrative between them (reusing the exact same sentence-boundary
    /// test <see cref="SceneEventPostProcessor"/> uses to decide whether two scene candidates
    /// are part of one continuous passage), since a dense run of suggestive lines can itself
    /// be building toward a scene; and any weak mention that shares a passage with a stronger
    /// label (nudity, implied or explicit activity, or an already-flagged complete scene) --
    /// a passing reference next to real activity is context for that activity, not noise.
    /// </remarks>
    internal static IReadOnlyList<ScanEvent> ExcludeLoneWeakSingletons(
        IReadOnlyList<ScanEvent> candidates,
        IReadOnlyList<TranscriptSegment> segments)
    {
        var strongEventIDs = new[]
        {
            "sexual_nudity", "sexual_implied_activity", "sexual_explicit_activity",
            "sexual_complete_scene", "sexual_violence"
        }.Select(label => ContentTaxonomy.Mappings[label].EventID).ToHashSet();

        var ordered = candidates.OrderBy(item => item.StartTime).ToArray();
        var weakIndices = ordered
            .Select((item, index) => (item, index))
            .Where(pair => WeakSexualEventIDs.Contains(pair.item.EventID))
            .Select(pair => pair.index)
            .ToArray();
        if (weakIndices.Length == 0) return ordered;

        // Group the weak mentions into clusters the same way scene candidates are grouped:
        // consecutive weak mentions stay in one cluster unless a complete sentence of
        // ordinary narrative separates them.
        var clusters = new List<List<int>>();
        var currentCluster = new List<int> { weakIndices[0] };
        for (var position = 1; position < weakIndices.Length; position += 1)
        {
            var previousIndex = currentCluster[^1];
            var index = weakIndices[position];
            var clean = TranscriptSentenceBoundaries.HasClearSentenceBetween(
                ordered[previousIndex].EndTime, ordered[index].StartTime, segments);
            if (clean)
            {
                clusters.Add(currentCluster);
                currentCluster = [index];
            }
            else
            {
                currentCluster.Add(index);
            }
        }
        clusters.Add(currentCluster);

        var excluded = new HashSet<int>();
        foreach (var cluster in clusters)
        {
            if (cluster.Count >= 3) continue; // A dense cluster of weak mentions is kept.

            // A weak mention next to a stronger label anywhere in the same unbroken passage
            // is kept as context for that activity, checked the same way a cluster's own
            // members are: no complete sentence separating them.
            var coOccursWithStrongLabel = ordered.Any(other =>
                strongEventIDs.Contains(other.EventID) &&
                !SeparatedByClearSentence(ordered[cluster[0]], other, segments));
            if (coOccursWithStrongLabel) continue;

            foreach (var index in cluster) excluded.Add(index);
        }

        return ordered.Where((_, index) => !excluded.Contains(index)).ToArray();
    }

    /// <summary>
    /// Whether a complete sentence separates two events, checked in whichever time order
    /// they actually fall (the two are not assumed to already be ordered relative to each
    /// other, unlike a cluster's own consecutive members).
    /// </summary>
    private static bool SeparatedByClearSentence(
        ScanEvent first, ScanEvent second, IReadOnlyList<TranscriptSegment> segments)
    {
        var (earlier, later) = first.StartTime <= second.StartTime ? (first, second) : (second, first);
        return TranscriptSentenceBoundaries.HasClearSentenceBetween(
            earlier.EndTime, later.StartTime, segments);
    }

    /// <summary>
    /// Whether a Terra decision needs Sol's second opinion at all.
    /// </summary>
    /// <remarks>
    /// True for Terra's own <c>needsEscalation</c> flag, or for an accepted scene whose
    /// confidence fell short of <paramref name="confidenceThreshold"/>. A confident accept
    /// that skips Sol is not skipping confirmation: the boundary confirmation against the
    /// transcript's own words in <see cref="TryConfirmSceneBoundary"/> still runs for it,
    /// exactly as it does for a Sol-reviewed candidate, using Terra's own reported quote.
    ///
    /// An ordinary rejected candidate (neither accepted nor flagged for escalation) needs
    /// nothing further -- it was never going to produce a scene event either way, and
    /// re-litigating every Terra "no" at Sol would multiply cost without changing an outcome
    /// Terra was clear about.
    ///
    /// <paramref name="isKeywordSafetyNetCandidate"/> is the one exception: a rejection of a
    /// candidate Luna's own first pass never proposed at all (see
    /// <see cref="AddUncoveredSexualCandidates"/>) is not "Terra was clear about this" in the
    /// same sense -- it is the pipeline's only chance to catch something Luna missed
    /// entirely, decided by a single call with no seed/temperature control over the model's
    /// own call-to-call variance. A rescan of ACOTAR Part 1 during this fix's own
    /// verification showed the same passage accepted by Terra on one call and rejected on
    /// another with nothing else in the pipeline changed. Giving Sol -- the stricter, final
    /// arbiter tier -- one independent second opinion on a safety-net rejection costs one
    /// extra call only on the rare passage the keyword scan actually flags as uncovered, not
    /// on the whole book.
    /// </remarks>
    internal static bool NeedsSolReview(
        VerifiedSceneCandidate decision, double confidenceThreshold, bool isKeywordSafetyNetCandidate) =>
        decision.NeedsEscalation ||
        (decision.Accepted && decision.Confidence < confidenceThreshold) ||
        (isKeywordSafetyNetCandidate && !decision.Accepted);

    private bool NeedsSolReview(VerifiedSceneCandidate decision, bool isKeywordSafetyNetCandidate) =>
        NeedsSolReview(decision, options.SolEscalationConfidenceThreshold, isKeywordSafetyNetCandidate);

    /// <summary>
    /// The two lanes' finalized confidence floor. Kept at the pre-existing 0.85 rather than
    /// the general 0.55 event floor for both lanes: a scene-level skip already carries more
    /// weight than a single detected line, and sexual_violence in particular must not be
    /// reported to a listener as confirmed on weaker evidence than a consensual scene is.
    /// </summary>
    private const double SceneAcceptanceConfidenceFloor = .85;

    /// <summary>
    /// Decides whether a verified candidate becomes a finalized event at all, and if so
    /// which label and taxonomy mapping it finalizes under.
    /// </summary>
    /// <remarks>
    /// The two lanes require different evidence because they are answering different
    /// questions: the consensual lane asks whether a sexual act is happening and is sustained
    /// beyond kissing; the sexual_violence lane asks the same about the act, plus whether the
    /// passage itself establishes it was non-consensual. Mixing the two checks would let an
    /// ordinary consensual scene satisfy the violence lane's threshold, or let a genuine
    /// assault scene's evidence be judged by a check that never asks about consent at all --
    /// which is exactly the ambiguity the two labels exist to resolve rather than reproduce.
    /// </remarks>
    internal static (string Label, TaxonomyMapping Mapping)? ResolveSceneOutcome(
        string firstPassLane, VerifiedSceneCandidate verification)
    {
        if (!verification.Accepted || !verification.DirectSexualActEvidence ||
            !verification.SustainedBeyondKissing ||
            verification.Confidence < SceneAcceptanceConfidenceFloor)
        {
            return null;
        }

        if (firstPassLane == "sexual_violence")
        {
            return verification.NonconsensualEvidence
                ? ("sexual_violence", ContentTaxonomy.Mappings["sexual_violence"])
                : null;
        }

        return ("sexual_complete_scene", ContentTaxonomy.Mappings["sexual_complete_scene"]);
    }

    /// <summary>
    /// Finalizes a confirmed scene's boundary: the quote-confirmed start, a claimed end no
    /// earlier than that start, then up to one second of allowance on each side snapped to
    /// the nearest actual transcript word.
    /// </summary>
    /// <remarks>
    /// Extracted so this exact rule -- Sol's "1 second each end, snap to nearest word" --
    /// is directly testable on its own, independent of the full escalation pipeline around
    /// it.
    /// </remarks>
    internal static (double Start, double End) ResolveConfirmedSceneBoundary(
        double confirmedStart,
        double claimedEndTime,
        IReadOnlyList<TranscriptSegment> segments)
    {
        // The quote confirms where the activity begins; the claimed endTime has no quote of
        // its own, so it is kept as the far bound but never allowed before the confirmed start.
        var claimedEnd = Math.Max(confirmedStart, claimedEndTime);
        return TranscriptWordLocator.ExpandAndSnap(
            segments, confirmedStart, claimedEnd, maxExpansionSeconds: 1.0)
            ?? (confirmedStart, claimedEnd);
    }

    /// <summary>
    /// The full decision for one accepted scene: locate its supporting quote in the
    /// transcript and finalize the boundary, or reject the scene outright when the quote
    /// cannot be confirmed at all.
    /// </summary>
    /// <remarks>
    /// No flat ±30s clamp any more. A previous version of this pipeline fell back to the
    /// first pass's originally proposed range whenever the refined quote could not be
    /// confirmed; that meant an accepted scene always produced some boundary. This version
    /// rejects the scene outright instead -- a finalized boundary this pipeline cannot
    /// confirm against the transcript's own words must not reach a listener presented as
    /// confirmed, so "no usable evidence" now means no event rather than an unconfirmed one.
    /// </remarks>
    internal static (double Start, double End)? TryConfirmSceneBoundary(
        string? quote,
        double claimedEndTime,
        IReadOnlyList<TranscriptSegment> segments)
    {
        if (string.IsNullOrWhiteSpace(quote)) return null;
        var located = TranscriptWordLocator.FindPhraseInSegments(segments, quote);
        if (located is null) return null;
        return ResolveConfirmedSceneBoundary(located.StartTime, claimedEndTime, segments);
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

    internal static IReadOnlyList<SceneVerificationCandidate> CoalesceSceneCandidates(
        IReadOnlyList<SceneVerificationCandidate> candidates)
    {
        const double mergeGapSeconds = 45;
        var result = new List<SceneVerificationCandidate>();

        // Coalesced independently per first-pass lane. A nearby sexual_violence candidate
        // and a consensual sexual_complete_scene candidate are never merged into one review
        // window even if they sit close in time -- Luna already treated them as mutually
        // exclusive, and merging them here would ask Terra to review two different claims
        // (assault, and ordinary romance) as if they were one passage.
        foreach (var lane in candidates.GroupBy(item => item.FirstPassLane, StringComparer.Ordinal))
        {
            var ordered = lane.OrderBy(item => item.ProposedStartTime).ToArray();
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
                    start, end, mergedSegments, first.FirstPassLane));
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
        }
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

    /// <summary>
    /// The shared closing instructions for both verification prompts below: boundary
    /// refinement, safeDescription rules, and the quote used to confirm the boundary.
    /// </summary>
    /// <remarks>
    /// Kept identical across both lanes deliberately. The two lanes disagree about what
    /// counts as evidence, not about how a confirmed range is reported once accepted.
    /// </remarks>
    private const string SceneVerificationClosingInstructions = """
For an accepted candidate, refine startTime to the beginning of the activity
or its immediate unmistakable lead-in, and endTime where that activity clearly finishes.
Keep timestamps within the supplied excerpt. Use a neutral, non-graphic but useful description.
Do not include graphic details or quotations in safeDescription. Never name intimate
anatomy or describe touching mechanics, positions, squeezing, or similar physical details.
Also return quote: for an accepted candidate, the exact consecutive words, copied verbatim
from a single segment's text, that begin the activity at your refined startTime -- this is not
shown to a listener, it is how the server confirms your refined boundary against the
transcript's own word timing. For a rejected candidate return an empty string. Return one
decision for every candidateKey, including nonconsensualEvidence (false when not applicable).
""";

    private async Task<SceneVerificationPayload> VerifySceneBatch(
        IReadOnlyList<SceneVerificationCandidate> candidates,
        string model,
        CancellationToken cancellationToken)
    {
        // Batches here are always a single candidate (Chunk(1) at every call site), so one
        // lane's candidates are never mixed with the other's in the same request -- each
        // request asks exactly one question, consensual-scene evidence or non-consent
        // evidence, never both at once.
        var isSexualViolenceLane = candidates.Count > 0 &&
            candidates.All(item => item.FirstPassLane == "sexual_violence");

        var input = (isSexualViolenceLane
            ? """
Act as a strict final verifier for one audiobook sexual-violence skip range (rape or sexual
assault). Each candidate was produced by a first-pass detector and may be a false positive, or
may in fact describe consensual activity that was mislabeled.

Set directSexualActEvidence=true only when this candidate's own transcript directly supports an
ongoing sexual act, imposed attempt, or credible immediate threat of one. Set
nonconsensualEvidence=true only when the passage itself establishes the act was without consent:
refusal, resistance, incapacitation, explicit coercion, or an exploited power imbalance used to
force the act. Judge consent from what the passage itself shows, never from a character's
profession, relationship to another character, or the setting alone. Set sustainedBeyondKissing
the same way the consensual lane would: true when the passage goes beyond attraction, dialogue,
kissing, embracing, or nudity alone into an actual act.

accepted may be true only when directSexualActEvidence, nonconsensualEvidence, and
sustainedBeyondKissing are ALL true, and confidence is at least 0.85. If the passage instead
reads as consensual, or if consent is genuinely ambiguous rather than clearly absent, accepted
must be false -- ambiguous consent is not evidence of assault and must not be reported as one.
Set needsEscalation=true only when the candidate is still a plausible non-consensual act but the
evidence, confidence, or exact boundaries are uncertain and require a stronger final review; set
it false for clear rejections, ambiguous-consent passages, or passages that read as consensual.
Confirmed accepted candidates also receive final review.
"""
            : """
Act as a strict final verifier for one audiobook sexual-scene skip range.
Each candidate was produced by a high-recall detector and may be a false positive.
Set directSexualActEvidence=true only when this candidate's own transcript directly supports
an ongoing sexual act. Set sustainedBeyondKissing=true when the passage goes beyond
attraction, dialogue, kissing, embracing, or nudity alone into an actual sexual act. It asks how
far the passage goes, NOT how long it lasts: a brief encounter still qualifies. A listener who
switched on Complete sex scenes is asking for sex scenes to be gone, and a short one rejected here
is exactly the scene that then plays. A discussion
of past sex is a reference, not an ongoing act. Flirting, suggestive language, attraction,
kissing alone, embraces, nudity alone, sexual jokes or references, profanity, medical
discussion, violence, combat, pain, breathing, groaning, or the word "thrust" in a non-sexual
context must be rejected. Do not infer an act from tone, romance, or physical closeness.
Do not require graphic anatomical vocabulary. In context, physical sexual escalation such as
intimate touching (for example a hand moving onto a thigh), opening or spreading legs, removing
clothing, intimate caressing, or explicit consent/positioning is direct evidence when it is part
of an ongoing sexual encounter. A combination of these cues must not be downgraded merely because
the narration is euphemistic or non-graphic. This lane is for consensual activity only: if the
passage instead shows the act was non-consensual, reject it here (accepted=false) rather than
reclassifying it -- a separate sexual-violence review handles that case.

accepted may be true only when BOTH evidence booleans are true and confidence is at least
0.85. Otherwise accepted must be false. Set needsEscalation=true only when the candidate is
still a plausible ongoing sexual scene but the evidence, confidence, or exact boundaries are
uncertain and require a stronger final review. Set needsEscalation=false for clear rejections,
isolated innuendo, references, attraction, kissing, or nudity alone. Confirmed accepted scenes
will also receive final review. Confidence must describe the evidence that a sexual act occurs,
not merely for one suggestive word, and not for how long it lasts.
""") + SceneVerificationClosingInstructions + """

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
        for (var index = 0; index < ContentTaxonomy.ModelEmittableLabels.Count; index += 1)
        {
            var label = ContentTaxonomy.ModelEmittableLabels[index];
            var last = index == ContentTaxonomy.ModelEmittableLabels.Count - 1;
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
reference removes narration the listener wanted to hear. 
The six sexual levels below are a ladder, and each rung means one thing. A listener switches on
the level they are not willing to hear, so a passage placed a rung too high is removed from
someone who wanted it, and a rung too low is heard by someone who did not. Choose the highest
rung the passage actually reaches, and only that one, except where a complete scene is also
required below. sexual_violence sits apart from this ladder entirely; see its own definition
below for when it replaces the ladder rather than adding to it.

sexual_suggestive_dialogue -- flirtation, innuendo, wanting, tension. Kissing and embracing belong
here, however charged, and so does a passage that is only anticipation. Kissing is NOT explicit
activity at any intensity.

sexual_references -- sex spoken about rather than happening: a past encounter recalled, a crude
joke, a comment on someone's history, an offer not taken up.

sexual_nudity -- a body described unclothed, or clothing being removed, with no sexual act
following in this passage. Undressing on its own is this rung, not explicit activity.

sexual_implied_activity -- sex happens and the narration does not describe it. It fades out, cuts
away, or resumes afterwards: "later, tangled in the sheets". The act is certain, the description
is absent.

sexual_explicit_activity -- a sexual act described as it happens. Intercourse, oral sex, or
equivalent, narrated in the moment. Euphemism still counts when the act is unmistakable. Kissing,
undressing, or a hand on a thigh do not reach this rung on their own; the passage has to convey
that a sexual act is taking place.

sexual_complete_scene -- the whole span of a scene containing implied or explicit activity, from
its clear lead-in to the point where the story returns to non-sexual action or conversation.
Reserved for consensual activity; see sexual_violence below for the non-consensual case.

sexual_violence -- rape or sexual assault: a sexual act, or the credible immediate threat or
attempt of one, imposed on a character who does not consent, cannot consent, or whose consent
is coerced or withdrawn and disregarded. This includes an assault narrated as it happens, one
implied or faded out the same way sexual_implied_activity is, and one described only
afterward provided the passage makes clear that it happened without consent. Judge consent from
what the passage itself establishes -- refusal, resistance, incapacitation, coercion, or an
imbalance of power exploited to force the act -- not from a character's profession, relationship
to another character, or the setting alone. A passage that is ambiguous about consent, or where
the narration itself is uncertain, is not sexual_violence; use the ordinary ladder instead and
let a human reviewer resolve the ambiguity. This is its own category, not a rung on the ladder
above: a passage is sexual_violence or it is a consensual sexual_suggestive_dialogue /
sexual_references / sexual_nudity / sexual_implied_activity / sexual_explicit_activity /
sexual_complete_scene, never both. Do not emit sexual_complete_scene for a passage reported as
sexual_violence, and do not emit sexual_violence for a passage that is ordinary threat, assault,
or violence with no sexual element -- that remains a violence label if it qualifies for one.
Treat this with at least the same care as violence_graphic: report it when the passage actually
depicts or credibly implies it, and do not infer it from a menacing tone, a violent scene that
happens to involve two characters, or a relationship's power imbalance on its own.

No word is sexual content by itself. A body part named in passing is anatomy, not a sexual
reference: a hand on a chest during first aid, a breast wound in battle, a character washing, a
mother nursing, a medical examination. Judge the passage by what is happening in it, never by the
presence of a word. The same applies to violence and every other category -- a word is evidence
only in the sense the passage actually uses it.

ALWAYS emit sexual_complete_scene alongside sexual_implied_activity or sexual_explicit_activity
for a CONSENSUAL scene, every time, including a brief encounter and one whose description is
euphemistic. A listener who switches on Complete sex scenes is asking for sex scenes to be gone;
a scene that was too short or too discreet to qualify is exactly the one that then plays and is
heard. Do not reduce a scene to the single explicit sentence that made it recognisable, and do
not withhold the scene event because the scene was small. A sexual_violence passage never also
receives sexual_complete_scene -- report sexual_violence alone, spanning the same full extent a
complete scene would (lead-in through to the point the story returns to non-sexual action).

If a sexual scene was already underway at the first supplied segment, set the scene start to
that first segment's startTime. If it is still underway at the last supplied segment, set its
end to that last segment's endTime. The server analyzes overlapping windows and will join those
partial ranges. A scene can span several minutes. Consensual romance, foreplay, implied acts,
explicit acts, and the immediate aftermath may establish continuity even when no explicit word
appears in every segment; the same applies to a sexual_violence passage and its own immediate
aftermath. Do not extend a scene across a clear topic, location, time, or chapter change.
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
For every event, also return quote: the exact consecutive words from the supplied transcript
text that support this event, copied verbatim from a single segment's text field, not
paraphrased, summarized, or combined across segments. This is not shown to any listener; it is
how the server locates the words you mean. A short event's quote should be just the words that
carry it, matching startTime and endTime -- three words for a three-word event, not the whole
sentence around them. A longer scene's quote should be the clearest single supporting sentence
or clause inside it, near where you believe the scene most clearly qualifies; it does not need
to span the whole scene, and the server uses startTime and endTime for the outer boundary. If
you cannot point to specific words that justify an event, do not report the event.
For profanity labels only, return the exact single profane word in profanityWord so the server
can censor and count it; otherwise return null. The profane word should also be the quote.
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
                        "safeDescription", "profanityWord", "quote"),
                    ["properties"] = new JsonObject
                    {
                        ["label"] = new JsonObject
                        {
                            ["type"] = "string",
                            // Derived from the taxonomy so the schema cannot permit a label
                            // the taxonomy would then discard, and narrowed to the labels
                            // Luna itself may propose -- profanity is excluded here even
                            // though the app still offers its switches; see
                            // ContentTaxonomy.ModelEmittableLabels.
                            ["enum"] = new JsonArray(ContentTaxonomy.ModelEmittableLabels
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
                        },
                        // Never shown to a listener. This is how AddEvent locates the exact
                        // words that justify the event in the transcript's own word timing,
                        // rather than trusting startTime/endTime as invented numbers.
                        ["quote"] = new JsonObject { ["type"] = "string", ["maxLength"] = 200 }
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
                        "sustainedBeyondKissing", "nonconsensualEvidence", "startTime", "endTime",
                        "confidence", "safeDescription", "quote"),
                    ["properties"] = new JsonObject
                    {
                        ["candidateKey"] = new JsonObject { ["type"] = "string" },
                        ["accepted"] = new JsonObject { ["type"] = "boolean" },
                        ["needsEscalation"] = new JsonObject { ["type"] = "boolean" },
                        ["directSexualActEvidence"] = new JsonObject { ["type"] = "boolean" },
                        ["sustainedBeyondKissing"] = new JsonObject { ["type"] = "boolean" },
                        // Only meaningful for a candidate reviewed in the sexual_violence
                        // lane; false is always correct for a consensual-lane candidate and
                        // plays no part in that lane's decision. See ResolveSceneOutcome.
                        ["nonconsensualEvidence"] = new JsonObject { ["type"] = "boolean" },
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
                        },
                        // Never shown to a listener. Confirms the refined startTime against
                        // the transcript's own word timing before it is trusted.
                        ["quote"] = new JsonObject { ["type"] = "string", ["maxLength"] = 200 }
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
        [property: JsonPropertyName("profanityWord")] string? ProfanityWord,
        /// <summary>
        /// The exact words the model says support this event, copied verbatim from the
        /// transcript. Never shown to a listener; used only to locate the event's real
        /// word-level timing before the model's own startTime/endTime are trusted.
        /// </summary>
        [property: JsonPropertyName("quote")] string? Quote = null);

    internal sealed record SceneVerificationCandidate(
        [property: JsonPropertyName("candidateKey")] string CandidateKey,
        [property: JsonPropertyName("proposedStartTime")] double ProposedStartTime,
        [property: JsonPropertyName("proposedEndTime")] double ProposedEndTime,
        [property: JsonPropertyName("segments")] IReadOnlyList<TranscriptSegment> Segments,
        /// <summary>
        /// "sexual_complete_scene" or "sexual_violence" -- which lane Luna's first pass
        /// placed this candidate in. Not shown to the model or trusted as a final answer;
        /// only used so two candidates from different lanes are never coalesced into one
        /// review window, since Terra/Sol's own consent-related verdict is what actually
        /// decides the finalized label.
        /// </summary>
        [property: JsonIgnore] string FirstPassLane = "sexual_complete_scene");

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

    internal sealed record VerifiedSceneCandidate(
        [property: JsonPropertyName("candidateKey")] string CandidateKey,
        [property: JsonPropertyName("accepted")] bool Accepted,
        [property: JsonPropertyName("needsEscalation")] bool NeedsEscalation,
        [property: JsonPropertyName("directSexualActEvidence")] bool DirectSexualActEvidence,
        [property: JsonPropertyName("sustainedBeyondKissing")] bool SustainedBeyondKissing,
        [property: JsonPropertyName("startTime")] double StartTime,
        [property: JsonPropertyName("endTime")] double EndTime,
        [property: JsonPropertyName("confidence")] double Confidence,
        [property: JsonPropertyName("safeDescription")] string SafeDescription,
        /// <summary>
        /// The words the verifier says begin the activity at its refined startTime. Confirmed
        /// against the transcript before the refined boundary is trusted; see AddEvent's
        /// sibling logic in AnchorToTranscript, which this mirrors on a smaller scale.
        /// </summary>
        [property: JsonPropertyName("quote")] string? Quote = null,
        /// <summary>
        /// True only when the candidate is being reviewed under the sexual_violence lane and
        /// the passage itself establishes the act was non-consensual. Always false for a
        /// candidate reviewed under the consensual sexual_complete_scene lane, where it plays
        /// no part in the decision -- see <see cref="ResolveSceneOutcome"/> for how the two
        /// lanes' evidence requirements differ.
        /// </summary>
        [property: JsonPropertyName("nonconsensualEvidence")] bool NonconsensualEvidence = false);

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
