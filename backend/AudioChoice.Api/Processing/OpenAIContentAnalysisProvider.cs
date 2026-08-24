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
    HttpClient client,
    OpenAIProcessingOptions options,
    AudioChoice.Api.Services.AudioChoiceDataPaths dataPaths,
    ILogger<OpenAIContentAnalysisProvider> logger) : IContentAnalysisProvider
{
    // Bump this whenever the baseline classification policy changes so cached batch
    // answers cannot silently reintroduce events produced under an older policy.
    private const string BaseAnalysisPromptVersion = "2.4-strict-sexual-escalation";
    private const string SceneVerificationVersion = "3.3-explicit-sol-routing";
    private const string SceneEscalationVersion = "3.2-parallel-sol";
    private readonly string _checkpointFolder = dataPaths.AnalysisCheckpoints;
    public string ScannerVersion => options.ScannerVersion;

    public async Task<IReadOnlyList<ScanEvent>> Analyze(
        IReadOnlyList<TranscriptSegment> segments,
        Action<double>? reportProgress,
        CancellationToken cancellationToken)
    {
        var events = new List<ScanEvent>();
        var deterministic = DeterministicContentDetector.Detect(segments);
        for (var index = 0; index < deterministic.Count; index += 1)
        {
            var item = deterministic[index];
            AddEvent(events, item.Label, item.StartTime, item.EndTime,
                item.Confidence, item.SafeDescription, item.ProfanityWord,
                item.StartTime, item.EndTime, $"deterministic-{index}");
        }
        logger.LogInformation(
            "Deterministic analysis found {EventCount} events in {SegmentCount} transcript segments.",
            deterministic.Count,
            segments.Count);

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
            return ApplyCompleteSceneSafetyGuard(lambdaResult, segments);
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

            foreach (var item in classified.Events)
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
        // Do not allow broad/ordinary violence categories into a new filter profile.
        // The app's Violence switch is intentionally reserved for graphic material,
        // torture, violence involving children or animals, and suicide/self-harm.
        uniqueEvents = ApplyNarrowViolencePolicy(uniqueEvents);
        var verifiedEvents = await VerifyCompleteSexualScenes(
            uniqueEvents, segments,
            progress => reportProgress?.Invoke(.75 + progress * .25),
            cancellationToken);
        var result = SceneEventPostProcessor.Process(verifiedEvents, segments).ToArray();
        result = ApplyCompleteSceneSafetyGuard(result, segments);

        if (segments.Count >= 500 && result.Length == 0 &&
            DeterministicContentDetector.ContainsObviousContent(segments))
        {
            throw new InvalidOperationException(
                "Content analysis produced an implausible empty result for a transcript containing explicit indicators.");
        }

        logger.LogInformation(
            "Content analysis completed with {EventCount} unique events.",
            result.Length);
        return result;
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

    private ScanEvent[] ApplyCompleteSceneSafetyGuard(
        IReadOnlyList<ScanEvent> events,
        IReadOnlyList<TranscriptSegment> segments)
    {
        if (segments.Count == 0) return events.ToArray();
        var mapping = ContentTaxonomy.Mappings["sexual_complete_scene"];
        var completeScenes = events.Where(item => item.EventID == mapping.EventID).ToArray();
        var audiobookDuration = Math.Max(1, segments.Max(item => item.EndTime) -
            segments.Min(item => item.StartTime));
        var skippedDuration = completeScenes.Sum(item => Math.Max(0, item.EndTime - item.StartTime));

        // Broad scene skips can hide minutes of narration, so an obviously implausible
        // verifier result must fail closed. Narrow explicit-content events remain available;
        // only the unsafe broad ranges are suppressed until a later scanner can review them.
        if (completeScenes.Length <= 25 && skippedDuration <= audiobookDuration * .20)
        {
            return events.ToArray();
        }

        logger.LogError(
            "Suppressed {SceneCount} complete sexual-scene ranges covering {SkippedSeconds:F0} " +
            "of {AudiobookSeconds:F0} seconds because the result exceeded the safety limit.",
            completeScenes.Length, skippedDuration, audiobookDuration);
        return events.Where(item => item.EventID != mapping.EventID).ToArray();
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

    private static void AddEvent(
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
        if (!ContentTaxonomy.Mappings.TryGetValue(label, out var mapping)) return;
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
    }

    private static string SafeDescriptionForEvent(string label, string? supplied)
    {
        if (!string.Equals(supplied, "Local Lambda content cue", StringComparison.Ordinal) &&
            !string.Equals(supplied, "Lambda sexual-content candidate window", StringComparison.Ordinal))
        {
            return SafeDescription(supplied);
        }

        return label switch
        {
            "sexual_suggestive_dialogue" => "Suggestive dialogue or innuendo detected",
            "sexual_references" => "Sexual references or suggestive dialogue detected",
            "sexual_nudity" => "Nudity described",
            "sexual_implied_activity" => "Implied sexual activity described",
            "sexual_explicit_activity" => "Sexual activity described",
            "sexual_complete_scene" => "Sustained consensual sexual activity following intimate escalation",
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

    private async Task<AnalysisPayload> AnalyzeBatch(
        IReadOnlyList<TranscriptSegment> segments,
        CancellationToken cancellationToken)
    {
        var input = BuildInput(segments);
        var body = BuildRequestBody(input);

        for (var attempt = 0; ; attempt += 1)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "responses");
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                options.ApiKey);
            request.Content = new StringContent(
                body.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using var response = await client.SendAsync(
                request,
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync(
                    cancellationToken);
                var outputText = ExtractOutputText(responseJson);

                return JsonSerializer.Deserialize<AnalysisPayload>(outputText)
                    ?? throw new InvalidOperationException(
                        "Content analysis returned no structured result.");
            }

            if (attempt >= options.MaximumRetries ||
                (response.StatusCode != HttpStatusCode.TooManyRequests &&
                 (int)response.StatusCode < 500))
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException(
                    $"Content analysis failed with HTTP {(int)response.StatusCode}: {error}");
            }

            var delay = response.Headers.RetryAfter?.Delta
                ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));

            logger.LogWarning(
                "Content analysis retry {Attempt} after {Delay}.",
                attempt + 1,
                delay);

            await Task.Delay(delay, cancellationToken);
        }
    }

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
            .Take(options.MaximumSceneEscalationRequestsPerJob)
            .ToArray();

        logger.LogInformation(
            "Sol escalation planned: {SolCandidateCount} confirmed or ambiguous scene candidates " +
            "from {TerraCandidateCount} Terra decisions; concurrency {SolConcurrency}.",
            escalationCandidates.Length, terraDecisions.Length,
            Math.Max(1, options.SceneEscalationConcurrency));

        var solDecisions = await RunSolEscalations(
            escalationCandidates,
            progress => reportProgress?.Invoke(.5 + progress * .5),
            cancellationToken);
        var solByCandidate = solDecisions.ToDictionary(
            item => item.CandidateKey, StringComparer.Ordinal);

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
                SafeDescription(verification.SafeDescription)));
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
or quotations. Return one decision for every candidateKey.

Candidates:
""" + JsonSerializer.Serialize(candidates);
        var body = BuildSceneVerificationRequestBody(input, model);

        for (var attempt = 0; ; attempt += 1)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "responses");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
                return JsonSerializer.Deserialize<SceneVerificationPayload>(ExtractOutputText(responseJson))
                    ?? throw new InvalidOperationException("Scene verification returned no structured result.");
            }
            if (attempt >= options.MaximumRetries ||
                (response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500))
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new HttpRequestException(
                    $"Scene verification failed with HTTP {(int)response.StatusCode}: {error}");
            }
            var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static string BuildInput(
        IReadOnlyList<TranscriptSegment> segments)
    {
        var transcript = JsonSerializer.Serialize(segments);

        return """
Act as an audiobook content-preference classifier. Identify only events that are explicitly
supported by the supplied transcript. Be conservative for violence: a listener who enables
Violence wants only graphic/gory injury, torture, violence involving children or animals, or
self-harm/suicide. Do not flag ordinary conflict, threats, combat without graphic detail,
non-graphic injuries, scars, medical discussion, pain, injury aftermath, death references, or
fantasy danger. When the evidence is not clearly within the narrow categories below, omit it.
For isolated events, return the narrowest supported timestamps. Sexual activity is different:
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
sexual_suggestive_dialogue, sexual_references, sexual_nudity, sexual_implied_activity,
sexual_explicit_activity, sexual_complete_scene, profanity_mild, profanity_strong,
profanity_sexual, profanity_slur, violence_graphic, violence_torture,
violence_children, violence_animals,
substance_alcohol_use, substance_intoxication, substance_drug_reference,
substance_drug_use, substance_abuse_overdose, blasphemy_religious_profanity,
blasphemy_statement, self_harm_reference, self_harm_suicidal_thoughts,
self_harm_suicide_attempt, self_harm_depiction.
For safeDescription, write a neutral, non-graphic summary of at most 80 characters.
Do not quote transcript text. For profanity labels only, return the exact single profane word
in profanityWord so the server can censor and count it; otherwise return null.
For profanity, emit one event for every occurrence so playback can skip each timestamp; the
app will group all occurrences of the same word under one switch. For longer sexual, violent, substance,
or self-harm scenes, emit one event spanning the complete supported scene rather than one
event per sentence. Do not invent content or identifiers. Omit events below 0.55 confidence.

Transcript segments:
""" + transcript;
    }

    private JsonObject BuildRequestBody(string input) => new()
    {
        ["model"] = options.AnalysisModel,
        ["input"] = input,
        ["text"] = new JsonObject
        {
            ["format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["name"] = "audiochoice_scan_events",
                ["strict"] = true,
                ["schema"] = new JsonObject
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
                                        ["enum"] = new JsonArray(
                                            "sexual_suggestive_dialogue", "sexual_references",
                                            "sexual_nudity", "sexual_implied_activity",
                                            "sexual_explicit_activity", "sexual_complete_scene",
                                            "profanity_mild", "profanity_strong", "profanity_sexual", "profanity_slur",
                                            "violence_graphic", "violence_torture", "violence_children", "violence_animals",
                                            "substance_alcohol_use", "substance_intoxication", "substance_drug_reference",
                                            "substance_drug_use", "substance_abuse_overdose",
                                            "blasphemy_religious_profanity", "blasphemy_statement",
                                            "self_harm_reference", "self_harm_suicidal_thoughts",
                                            "self_harm_suicide_attempt", "self_harm_depiction")
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
                }
            }
        }
    };

    private static JsonObject BuildSceneVerificationRequestBody(string input, string model) => new()
    {
        ["model"] = model,
        ["input"] = input,
        ["text"] = new JsonObject
        {
            ["format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["name"] = "audiochoice_scene_verification",
                ["strict"] = true,
                ["schema"] = new JsonObject
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
                }
            }
        }
    };

    private static string ExtractOutputText(string responseJson)
    {
        var root = JsonNode.Parse(responseJson)
            ?? throw new InvalidOperationException("Content analysis returned invalid JSON.");

        foreach (var output in root["output"]?.AsArray() ?? new JsonArray())
        {
            foreach (var content in
                output?["content"]?.AsArray() ?? new JsonArray())
            {
                if (content?["type"]?.GetValue<string>() == "output_text" &&
                    content["text"]?.GetValue<string>() is string text)
                {
                    return text;
                }
            }
        }

        throw new InvalidOperationException(
            "Content analysis response did not contain output text.");
    }

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
