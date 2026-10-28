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
    //
    // Bumped again: BuildInput's sexual-content ladder gained a worked example showing a
    // passage that escalates mid-passage from sexual_suggestive_dialogue into
    // sexual_implied_activity/sexual_complete_scene, so a candidate batch cached under the
    // prior version was classified without that guidance and may under-report exactly the
    // scenes this fix exists to catch.
    //
    // Bumped again: swapped the underlying model from QuantTrio/Qwen3.6-27B-AWQ (INT4) to
    // Qwen/Qwen3.6-27B-FP8, without options.AnalysisModel's own served-name string changing
    // (both answer to "qwen3.6-27b") -- the checkpoint key this version feeds would
    // otherwise silently keep serving the older, more heavily quantized model's answers
    // under a served name that now points at a materially different model. This is the
    // same failure this session already found and fixed once for a prompt change; a model
    // swap needs the identical protection.
    //
    // Bumped again: added sexual_kissing as its own rung, split out of
    // sexual_suggestive_dialogue, so a kiss the narration lingers on can be its own switch
    // separate from flirtatious dialogue (a real listener report: turning off suggestive
    // dialogue should not also silence kissing, and vice versa). A cached first-pass answer
    // from before this change never had sexual_kissing available to propose at all, and
    // would have folded a real kissing scene into sexual_suggestive_dialogue the old way.
    //
    // Bumped again: VllmModelClient now disables Qwen3.6's default "thinking" mode
    // (chat_template_kwargs enable_thinking=false) on every request through it, to fix
    // real production turnaround -- individual Terra/Sol verification calls were measured
    // taking 300-500+ seconds each purely on the model's own invisible reasoning trace, on
    // an answer that is a handful of structured fields. Whether the model reasoned before
    // answering is not observable in a cached checkpoint's own JSON, so a stale answer from
    // before this change could otherwise be silently trusted as though it came from the
    // same (slower, chain-of-thought) model this pipeline was evaluated and tuned against.
    //
    // Bumped again: removed the response schema's maxLength=80 bound on safeDescription (and
    // the unused maxLength on profanityWord/quote) -- a real, reproducible production bug,
    // not a quality tweak. vLLM's guided-decoding backend (xgrammar) silently drops a JSON
    // Schema string's maxLength keyword rather than enforcing or rejecting it (a documented
    // vLLM/xgrammar limitation), so the model had no actual signal to stop at 80 characters
    // and kept generating past the schema's own declared limit with nothing able to close
    // the string or object correctly -- corrupting the JSON deterministically, at the same
    // ~80th character, on every retry of the same candidate. SafeDescription already
    // truncates to 80 characters in code before anything is trusted, so the schema
    // constraint was never doing safety work; it was only breaking generation.
    //
    // Bumped again: the first-pass content-analysis call now runs at temperature 0.7
    // (Qwen's own documented non-thinking/instruct sampling value) instead of greedy
    // decoding, to recover first-pass recall on subtler sexual-content cues lost when
    // thinking mode was disabled (see VllmModelClient.CompleteJson's baseTemperature
    // remarks for the real production evidence). A cached checkpoint from before this
    // change was generated at temperature 0 and must not be silently trusted as though it
    // came from the same sampling this version now uses.
    //
    // Bumped again: MaximumSegmentsPerAnalysisRequest lowered from 100 to 60 (see that
    // field's own remarks) after a real 100-segment batch never converged against this
    // self-hosted model's context window regardless of retries. A checkpoint cached under
    // the prior version was built from a 100-segment batch boundary that no longer exists;
    // reusing it here would silently keep serving results sliced along boundaries this
    // version was specifically changed to avoid.
    // Bumped for the exposed-anatomy/nudity split. The anatomy carve-out told the model that "a
    // body part named in passing is anatomy, not a sexual reference" and listed only exempt
    // examples -- first aid, a battle wound, washing, nursing, a medical exam. Nothing said where
    // that exemption stops, so a passage describing intimate anatomy as bare or uncovered read as
    // the same "named in passing" case and returned no event at all, despite sexual_nudity's own
    // rung existing for exactly it. A real listener case: "the badge pinned to her breast" and
    // "her bare breast and nipples" were treated alike, when the first is anatomy and the second
    // is the thing a nudity filter is switched on for.
    //
    // The carve-out now names the clothed/functional cases explicitly (including a pin at the
    // breast and a breastplate, the two that misfire most in this library's own genre), and
    // sexual_nudity now states that bare, naked, uncovered or exposed intimate anatomy reaches
    // that rung on its own -- no act, no arousal, no partner reaction required. Both directions
    // are stated, because widening one without restating the other is how a fix for a miss
    // becomes a flood of false positives on armour and injuries.
    // Bumped for the self-harm rubric. Measured against the 35-case ground-truth set on the
    // production Qwen3.6-27B-FP8 host: 30/33 scored cases passed, and one of the three failures was
    // a passage recounting that captured resistance members used suicide capsules, reported as
    // self_harm_reference at 0.95. Self-harm is the one label group with no second verification
    // pass -- Luna's first call is the only judgement it ever gets -- and it was also the only group
    // with no written rubric at all, so "a passing mention of suicide" was left to mean whatever the
    // model took it to mean. Now states that these labels describe something a character does, and
    // that suicide or self-injury as a subject -- war dead, a faction's casualties, a method named
    // in the abstract, a figure of speech, or self-hatred with no self-injury -- is not this
    // category. The five self-harm positives in the set all describe a character's own acts and are
    // unaffected.
    // Bumped again together with ScannerVersion and MinimumEventConfidence, at the owner's
    // request to catch more sexual references and innuendo: sexual_suggestive_dialogue's
    // definition now gives concrete examples of euphemism and double entendre and explicitly
    // tells the model that an unmistakable innuendo does not need an explicit statement to be
    // reported, and the prompt's own confidence instruction now tells the model not to mark a
    // passage down purely for being implied rather than explicit. The enforced floor dropped
    // from 0.55 to 0.45 for the same reason -- implied content is inherently lower-confidence
    // than explicit content, and the old floor was discarding exactly the subtler cues this
    // change targets. A scan made under the prior version never had this wider definition or
    // this floor and must not be presented as though it had.
    //
    // Bumped again together with ScannerVersion, at the owner's request, to narrow the same
    // rung back down in three specific ways the widening above would otherwise over-flag.
    // Crude or vulgar language used for rivalry, insult, or combat -- not sexual in subject even
    // though it borrows sexual words -- stays unflagged, with a worked example from this
    // library's own genre (a boss-fight taunt). The same wording flips to this rung the moment
    // it actually sexualizes a specific person rather than describing a fight or a contest,
    // particularly dwelling on someone's body or desirability rather than their skill or ego.
    // And naming what a character does for a living -- including sex work -- is treated like
    // naming any other occupation, not sexual content, unless the passage goes on to actually
    // linger on it sexually rather than simply stating it. A scan made under the prior version
    // never had these carve-outs and would over-report exactly the three patterns above.
    //
    // Bumped again together with ScannerVersion, at the owner's explicit request, for a lower
    // bar specifically when a woman is the subject of a crude remark rather than a mutual
    // participant in it. The rivalry exemption just above is for two people trading the same
    // insult back and forth; a crude joke, taunt, or comment that instead uses a woman as its
    // subject or punchline -- about her body, what would be done to or with her, or treating her
    // as the prize rather than a party to the exchange -- does not get that exemption regardless
    // of how the surrounding dialogue frames it, and an ambiguous case is resolved in favor of
    // reporting it. A scan made under the prior version applied the rivalry exemption too
    // broadly to exactly this pattern.
    //
    // Bumped again together with ScannerVersion, at the owner's explicit request, to extend that
    // lower bar beyond the rivalry exemption specifically: any remark, innuendo, banter, comment
    // on a body part, or mention of nudity concerning a woman now reports at this rung or higher
    // even when mild or brief, with plain stated admiration (finding her beautiful, a simple
    // compliment on her looks) carved out explicitly so this does not sweep in ordinary attraction
    // that says nothing more about her body or what anyone wants from her. A scan made under the
    // prior version held a woman to the same bar as everything else on this rung and would miss
    // milder instances of exactly what this change targets.
    //
    // Bumped again together with ScannerVersion, at the owner's explicit, permanent request:
    // this deployment now filters sexual content only. Removed the violence_graphic/torture/
    // children/animals rubric and the self-harm rubric from the prompt entirely, removed the
    // profanity-specific closing instructions, and fixed every place the prompt cross-referenced
    // a now-out-of-scope category (sexual_violence no longer compares itself to violence_graphic;
    // the anatomy carve-out no longer says "the same applies to violence"). sexual_violence
    // (rape/sexual assault) itself is unaffected and keeps its full definition -- it is a sexual
    // label, not a violence label, and stays enforced; see ContentTaxonomy.EnforcedLabels's own
    // remarks for the explicit guard against ever confusing the two. A scan made under the prior
    // version was produced by a model that could still read, and occasionally act on, instructions
    // for categories this deployment no longer reports under any circumstance.
    // Bumped together with ScannerVersion and SceneCueRegex (DeterministicContentDetector.cs):
    // a completed scan (Stephen King's IT) reported no event for a passage using "blowjob"/
    // "blow job" and "clit", a real recall miss on exactly the kind of explicit, unambiguous
    // term the ladder's own instructions already say should be reported. Named both terms (and
    // the other common names for oral sex -- fellatio, cunnilingus, going down on) explicitly
    // in sexual_explicit_activity's own definition, and added the same terms to the keyword
    // safety net regex so a future miss on this specific wording is still caught even if the
    // model itself misses it again. A scan made under the prior version may be missing an
    // event for this exact wording and must not be presented as though this fix had already
    // applied to it.
    //
    // Bumped again together with ScannerVersion: the keyword safety net's SceneCueRegex was
    // substantially widened (anatomy slang, every common name for oral/anal sex, arousal and
    // foreplay language, and common literary fade-to-sex phrasing like "tangled in the
    // sheets") at the owner's explicit request to catch as much as possible, deliberately
    // erring toward high recall since this net only seeds a Terra candidate for review -- it
    // never creates an event by itself. The prompt text itself is unchanged by this bump; it
    // exists because a wider net on a rescan can surface events a narrower net's version
    // would never have found, and those must not be presented as though the narrower
    // version had already covered them.
    //
    // Bumped again for two structural recall fixes requested directly (not a keyword
    // change): ExcludeLoneWeakSingletons' dense-cluster threshold dropped from 3 to 2, so a
    // book that stays subtle throughout and never produces three weak mentions close
    // together no longer has every one of its real, separate flirtation/innuendo moments
    // discarded as "noise" two at a time. And NeedsSolReview no longer treats an ordinary
    // Terra rejection as final -- every rejection now gets one independent Sol second
    // opinion, the same treatment only keyword-safety-net rejections used to get, since the
    // sampling-variance risk that motivated that exception (this file's own ACOTAR Part 1
    // incident) applies to a Luna-proposed candidate's rejection exactly as much as a
    // safety-net seed's. A checkpoint cached under the prior version was filtered and
    // escalated under the old, narrower rules and must not be presented as though it had
    // already had the wider recall applied.
    //
    // Bumped again for two more fixes targeting the lighter sexual_* rungs specifically
    // (references, suggestive_dialogue, nudity, kissing, implied_activity), which -- unlike
    // sexual_complete_scene and sexual_violence -- get no Terra/Sol second opinion at all;
    // Luna's single first-pass call is final for them. First, sexual_references' own
    // definition was one sentence that directly overlapped sexual_suggestive_dialogue's own
    // worked examples (both named "a past encounter recalled" and "a comment on someone's
    // history" as their own), leaving the model no real way to choose between them on a
    // genuinely common case; it now states the actual distinguishing test (flat fact vs.
    // charged/suggestive tone) with a worked contrasting pair, and resolves a genuine tie
    // toward sexual_suggestive_dialogue specifically because that rung has no downstream
    // second check either way the ambiguity is resolved. Second, and more structurally: a
    // keyword-safety-net candidate that failed Terra/Sol's scene bar used to be discarded
    // outright with no event reported at all, even though Luna's own first pass had not
    // proposed anything for that window either -- the one gap this safety net exists to
    // close. It now falls back to sexual_implied_activity, sexual_nudity, or
    // sexual_suggestive_dialogue, chosen from Terra/Sol's own evidence booleans, rather than
    // vanishing. A scan made under the prior version applied the old rules and may be
    // missing events this version would now report.
    //
    // Bumped again for a real boundary bug in that same fallback, caught on a live rescan of
    // this deployment's own test book (Stephen King's IT) immediately after shipping it: the
    // fallback used the keyword-seeded candidate's full review-window bounds (up to several
    // minutes, the padding Terra needs to judge a full scene) as the reported event's own
    // boundary, producing dozens of 70-300 second "suggestive dialogue" skips for what was
    // really one line each -- a direct violation of this same prompt's own instruction that
    // a short reference must stay a short event. The fallback now re-locates the one segment
    // within that window that actually matched the keyword cue (see
    // DeterministicContentDetector.FindMatchingCueSegment) and anchors to that segment alone.
    // A scan made under the prior version has these oversized fallback events and must be
    // re-scanned to correct them, not merely re-run for new coverage.
    //
    // Bumped again for a real listener report on the prior fix's own replacement boundary:
    // "skipping right after it should have" -- the fallback had moved from the multi-minute
    // review window down to the matched segment's own StartTime/EndTime, but a transcript
    // segment is often a full sentence, and the matched cue word is not always at its start.
    // Reporting the segment's own boundary meant the skip could still start measurably after
    // the cue phrase itself had already begun playing. AddKeywordSafetyNetFallback now word-
    // snaps to the matched cue text's own span via TranscriptWordLocator.FindPhrase (the same
    // mechanism every other confirmed event in this pipeline already uses), widened by a
    // fixed 2-second pad on each side, rather than trusting any segment's own width. A scan
    // made under the prior version has fallback events boundaried to the matched segment,
    // not the matched words, and must be re-scanned to correct them.
    // Bumped to 7.0 for the three-category rewrite -- see OpenAIProcessingOptions.
    // ScannerVersion's own remarks for the full reasoning (narrowing EnforcedLabels alone
    // was not enough; the real fix required rewriting BuildInput's prompt text itself,
    // since the old prose kept teaching the model the removed eight-label vocabulary even
    // after the response schema's own enum was already narrowed to three labels).
    private const string BaseAnalysisPromptVersion = "7.0-three-category";
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
    //
    // Bumped again: SceneVerificationClosingInstructions' startTime refinement now asks for
    // the beginning of the continuous romantic/physical escalation into a scene, not only
    // the sentence containing its own unmistakable moment. A real listener report confirmed
    // the prior wording let a confirmed scene's boundary land well after the buildup a
    // listener would also expect skipped had already started playing -- a cached quote from
    // before this change reflects the old, narrower reading and must not be trusted as
    // though it already applied the new one.
    //
    // Bumped again: an escalated candidate's outcome is now a genuine three-vote majority
    // (Terra's own decision plus two independent Sol calls, see RunSolEscalations/
    // ResolveMajorityVote's remarks) rather than a single Sol call fully overriding Terra's.
    // A cached checkpoint from before this change holds only one Sol answer per candidate,
    // which this version bump keeps from being silently read as though it already reflected
    // a majority of three.
    // Bumped together with ScannerVersion for the switch to a self-hosted vLLM model
    // (Qwen3.6-27B) as the analysis transport, so a cached checkpoint from OpenAI's model is
    // never silently reused as though this different model had produced it.
    //
    // Bumped again: two real prompt fixes were made to VerifySceneBatch's consensual-lane
    // instructions and to Luna's first-pass sexual-content ladder instructions (see the
    // worked examples added for cross-segment quote recovery and for consensual-rough-play
    // vs non-consent), without this version changing. A real production consequence of that
    // miss: a rescan of a real book after the fix landed still silently reused a scene
    // candidate's checkpoint written under the OLD, unfixed prompt, producing a boundary
    // ~14 seconds later than the newly-fixed candidate that overlapped it -- because the
    // checkpoint key never changed, both the stale and the fresh answer were trusted as two
    // independent real candidates instead of one being recognized as obsolete. Every
    // classification-policy change here must bump this version; this is the exact failure
    // the pattern exists to prevent, that this same session's earlier changes broke by
    // omission.
    //
    // Bumped again together with BaseAnalysisPromptVersion: the underlying model swapped
    // from QuantTrio/Qwen3.6-27B-AWQ to Qwen/Qwen3.6-27B-FP8 without options.*Model's own
    // served-name strings changing, so this must be bumped explicitly rather than relying
    // on the model name itself to invalidate stale checkpoints.
    //
    // Bumped again together with BaseAnalysisPromptVersion: sexual_kissing joined
    // WeakSexualEventIDs alongside sexual_suggestive_dialogue and sexual_references, which
    // changes ExcludeLoneWeakSingletons' entry-gate behavior for a label that did not exist
    // under the prior version at all. Bumped so this policy change is never silently read
    // as though it already governed a checkpoint written before sexual_kissing existed.
    //
    // Bumped again together with BaseAnalysisPromptVersion: Qwen3.6's default reasoning is
    // now disabled on every VllmModelClient request (see that field's own remarks) to fix
    // real production turnaround on Terra/Sol verification calls specifically.
    //
    // Bumped again together with BaseAnalysisPromptVersion: removed
    // SceneVerificationResponseSchema's maxLength=80 bound on safeDescription, a real
    // production bug (see that field's own remarks) that was corrupting the JSON on any
    // candidate whose natural description landed past 80 characters, deterministically,
    // with no fix possible short of the model getting lucky with a shorter phrasing.
    // Bumped for the startTime lower bound. The buildup-inclusive rule said which escalation to
    // include and never which to exclude, and measured against the ground-truth set on the
    // production host it reached too far back on two of the three failures: a scene anchored 36s
    // early on whispered dialogue, and one anchored 79s early in a conversation about trust and a
    // past assault -- which that case's own notes call out as too early, since the physical scene
    // begins at the undressing that follows. Both were accepted correctly; only the boundary was
    // wrong. Now states that the escalation setting startTime is physical, and that dialogue does
    // not begin a scene however charged, with a worked example of the too-early mistake beside the
    // existing too-late one. The too-late rule is untouched -- that failure put audible content in
    // front of a listener and is the worse of the two.
    private const string SceneVerificationVersion =
        "6.7-physical-start-boundary";
    private const string SceneEscalationVersion =
        "6.6-schema-maxlength-fix";
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
        // Profanity and the Lambda-first category cues are both out of scope for this
        // deployment (see ContentTaxonomy.EnforcedLabels remarks): this filters sexual
        // content only, permanently, so neither deterministic source runs at all. Both
        // detectors still exist in DeterministicContentDetector purely so a scan made before
        // this change still resolves (its events reference ContentTaxonomy.Mappings entries
        // kept for exactly that), but nothing calls them to produce a new one.
        var deterministic = new List<DeterministicDetection>();
        logger.LogInformation(
            "Deterministic analysis skipped (profanity/category cues are out of scope for " +
            "this deployment); {SegmentCount} transcript segments go straight to the model.",
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
        // Violence is out of scope for this deployment entirely (see ContentTaxonomy's
        // EnforcedLabels remarks), so Luna's response schema cannot propose a violence_* label
        // in the first place -- there is nothing left here to narrow or verify.
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

    /// <summary>
    /// Discriminates the first pass's own second, independent call on a batch that already
    /// contains sexual content, from the first call's own checkpoint -- the same shape
    /// <see cref="SecondSolVoteDiscriminator"/> already uses for Sol's second escalation
    /// vote, so the two calls are never accidentally read back as the same cached answer.
    /// </summary>
    private const string SecondFirstPassVoteDiscriminator = "luna-vote-2";

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

            // A real production regression this second call exists to close: the same
            // batch, sent unchanged, escalated a passage to sexual_implied_activity on one
            // run and stayed at sexual_suggestive_dialogue -- the ladder's lowest rung -- on
            // a re-run, purely from ordinary model sampling variance on a genuinely close
            // call. Sol's own escalation lane already runs a real majority vote across
            // independent calls rather than trusting one answer (see ResolveMajorityVote);
            // the first pass never had that same protection until now. Gated to only the
            // batches that already contain at least one sexual-content event from the first
            // call -- doubling every batch in the book, including the many with no sexual
            // content at all, would double this pass's entire cost for a risk that is
            // specific to the sexual-content ladder, not to profanity, violence, substance,
            // or self-harm labels. A union rather than a strict majority: either call's
            // sexual-content events are kept, since Terra's own review downstream is what
            // actually decides a scene's fate -- an extra proposed candidate here costs one
            // more verification call, while a missed real escalation costs the finding
            // outright, and the second is the failure this pipeline treats as unacceptable.
            if (HasSexualContentEvent(classified))
            {
                var secondCheckpointPath = CheckpointPath(batch, SecondFirstPassVoteDiscriminator);
                var secondClassified = await LoadCheckpoint(secondCheckpointPath, cancellationToken);
                if (secondClassified is null)
                {
                    secondClassified = await AnalyzeBatch(batch, cancellationToken);
                    await SaveCheckpoint(secondCheckpointPath, secondClassified, cancellationToken);
                }
                else
                {
                    logger.LogInformation(
                        "Reused second first-pass checkpoint for batch {BatchNumber}; no API request was made.",
                        index + 1);
                }
                classified = MergeSexualContentEvents(classified, secondClassified);
            }

            completed();
            return new ContentBatchResult(index, batch, classified);
        }
        finally { gate.Release(); }
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
        // Was "sexual_complete_scene" -- see ResolveSceneOutcome's own remarks for why that
        // label is legacy now and never produced by a real scan. Updated to the label a real
        // accepted consensual scene actually finalizes under today.
        var mapping = ContentTaxonomy.Mappings["sexual_explicit_vulgar_scene"];
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

    private string CheckpointPath(
        IReadOnlyList<TranscriptSegment> segments, string? discriminator = null)
    {
        // Keep the expensive high-recall pass reusable across scanner releases. Scanner
        // 2.3 adds an independent verifier but intentionally reuses the completed 2.2
        // candidate checkpoints instead of paying to generate them again.
        var material = $"{options.AnalysisModel}|{BaseAnalysisPromptVersion}" +
            (discriminator is null ? "" : $"|{discriminator}") +
            $"|{JsonSerializer.Serialize(segments)}";
        var key = Hash(material);
        return Path.Combine(_checkpointFolder, $"{key}.json");
    }

    // Legacy labels kept alongside the three real ones so a checkpoint written under an
    // older scanner version (see CheckpointPath's own remarks on reusing prior candidate
    // checkpoints across releases) still resolves correctly; a real scan under the current
    // three-category prompt only ever produces sexual_explicit_vulgar_scene, sexual_nudity,
    // or sexual_violence here. Missing sexual_explicit_vulgar_scene entirely was a real bug
    // this list shared with the two near-identical lists below (see AddUncoveredSexualCandidates
    // and VerifyCompleteSexualScenes' own remarks for the production-visible symptom it caused).
    private static readonly HashSet<string> SexualContentLabels =
    [
        "sexual_suggestive_dialogue", "sexual_references", "sexual_nudity",
        "sexual_implied_activity", "sexual_explicit_activity", "sexual_complete_scene",
        "sexual_explicit_vulgar_scene", "sexual_violence", "sexual_kissing"
    ];

    internal static bool HasSexualContentEvent(AnalysisPayload payload) =>
        payload.Events.Any(item => SexualContentLabels.Contains(item.Label));

    /// <summary>
    /// Combines two independent first-pass answers for the same batch into one, keeping
    /// every sexual-content event either call proposed and every non-sexual event from the
    /// first call alone.
    /// </summary>
    /// <remarks>
    /// A union of sexual-content events, not a majority or an intersection: Terra's own
    /// review downstream is the real gate on whether a proposed event survives to a
    /// listener, so admitting an extra candidate here costs one more verification call
    /// while dropping a real one costs the finding outright. Non-sexual events are taken
    /// only from the first call, deliberately -- the second call exists to protect the
    /// sexual-content ladder specifically (see ProcessContentBatch's own remarks), not to
    /// double-detect profanity, violence, substance, or self-harm labels, where this
    /// pipeline has not measured the same run-to-run escalation inconsistency.
    /// </remarks>
    internal static AnalysisPayload MergeSexualContentEvents(
        AnalysisPayload first, AnalysisPayload second)
    {
        var merged = first.Events
            .Where(item => !SexualContentLabels.Contains(item.Label))
            .Concat(first.Events.Where(item => SexualContentLabels.Contains(item.Label)))
            .Concat(second.Events.Where(item => SexualContentLabels.Contains(item.Label)))
            .ToArray();
        return new AnalysisPayload(merged);
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
    ///
    /// Made configurable (AudioChoice:OpenAI:MaximumContextExpansionSeconds) rather than
    /// staying a fixed constant after a real failure: Luna's own batch size
    /// (MaximumSegmentsPerAnalysisRequest) was shrunk for a model with a smaller context
    /// window (Bedrock Qwen3-32B, 32,768 tokens vs. the self-hosted lane's 65,536), but Terra
    /// builds its own, independently-sized prompt via SentenceBoundedContext, and a dense
    /// candidate's context window still overflowed the smaller model's context outright
    /// even with Luna's own batches safely shrunk -- the two prompt-size knobs are
    /// independent and both need to move together for a smaller-context model.
    /// </remarks>
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
    ///
    /// <paramref name="maximumContextExpansionSeconds"/> defaults to the value every caller
    /// used before this became configurable (60); see
    /// OpenAIProcessingOptions.MaximumContextExpansionSeconds's own remarks for why a smaller-
    /// context-window model needs this (and Luna's separate MaximumSegmentsPerAnalysisRequest)
    /// shrunk together rather than leaving this a fixed constant.
    /// </remarks>
    internal static IReadOnlyList<TranscriptSegment> SentenceBoundedContext(
        double start,
        double end,
        IReadOnlyList<TranscriptSegment> segments,
        double maximumContextExpansionSeconds = 60)
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
            if (start - candidate.StartTime > maximumContextExpansionSeconds) break;
            firstIndex -= 1;
            // This segment itself closes a sentence, so the window now begins at the start
            // of a new one -- a natural place to stop expanding backward.
            if (TranscriptSentenceBoundaries.EndsWithSentenceEnd(candidate.Text)) break;
        }

        var lastIndex = highIndex;
        while (lastIndex < ordered.Length - 1)
        {
            var candidate = ordered[lastIndex + 1];
            if (candidate.EndTime - end > maximumContextExpansionSeconds) break;
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

        // Moved to ContentTaxonomy so the admin repair endpoint resolves the same string for
        // the same label; a hand-added event fixing a miss must be indistinguishable to a
        // listener from the scanned events on either side of it.
        return ContentTaxonomy.CanonicalDescription(label);
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
        AnalysisModelResponse response;
        try
        {
            response = await modelClient.CompleteJson(
                options.AnalysisModel,
                input,
                "audiochoice_scan_events",
                AnalysisResponseSchema(),
                cancellationToken);
        }
        catch (HttpRequestException error) when (
            segments.Count > 1 &&
            (error.Message.Contains("context-overflow retries") ||
             error.Message.Contains("minimum viable max_tokens")))
        {
            // Split rather than drop, and rather than fail the job. This is the detection
            // pass itself: abandoning the batch would leave every segment in it with no
            // events at all -- a silent unfiltered stretch of a real book, which is the one
            // outcome worse than a slow scan. Scene verification can afford to drop an
            // unverified candidate (see VerifySceneBatch's caller) because a dropped
            // candidate is merely not reported; here the batch *is* the reporting.
            //
            // Halves are independent: every event's timing comes from the segments it was
            // found in, so concatenating the two answers loses nothing except an event that
            // straddled the split point -- and the surrounding merge/scene passes already
            // stitch adjacent findings back together. Also worth noting that the observed
            // overflow is a vLLM-side misreport (the remarks on VerifySceneBatch's caller
            // document a 13,512-character passage claiming an impossible token count), so
            // re-asking with different content usually succeeds outright.
            var middle = segments.Count / 2;
            logger.LogWarning(
                error,
                "Content analysis batch of {SegmentCount} segments " +
                "({SpanStart:F0}s-{SpanEnd:F0}s) reported a context-length overflow; " +
                "splitting into {FirstCount} + {SecondCount} and retrying rather than " +
                "losing the batch's events.",
                segments.Count, segments[0].StartTime, segments[^1].EndTime,
                middle, segments.Count - middle);

            var firstHalf = await AnalyzeBatch(
                segments.Take(middle).ToArray(), cancellationToken);
            var secondHalf = await AnalyzeBatch(
                segments.Skip(middle).ToArray(), cancellationToken);
            return new AnalysisPayload([.. firstHalf.Events, .. secondHalf.Events]);
        }
        catch (HttpRequestException error) when (
            segments.Count == 1 &&
            (error.Message.Contains("context-overflow retries") ||
             error.Message.Contains("minimum viable max_tokens")))
        {
            // Floor of the split above: one segment that still cannot be answered. Failing
            // the whole job here would throw away a complete, otherwise-good scan of an
            // entire audiobook over a single transcript segment, so this yields no events
            // for that one segment and lets the scan finish. Logged as an error, not a
            // warning: it is a real blind spot, it names the exact seconds affected so it
            // can be re-checked by hand, and it must never pass unnoticed.
            logger.LogError(
                error,
                "Content analysis could not classify the single segment at " +
                "{SpanStart:F0}s-{SpanEnd:F0}s after exhausting every retry; it yields no " +
                "events and the scan continues. This span is unfiltered and needs review.",
                segments[0].StartTime, segments[0].EndTime);
            return new AnalysisPayload([]);
        }

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
        // Missing sexual_explicit_vulgar_scene here was a real bug: a real Luna-confirmed
        // scene under the current three-category prompt was never recognized as coverage,
        // so this method re-seeded a keyword-safety-net candidate directly on top of a scene
        // Luna had already proposed, for Terra to review all over again as a second,
        // separately-laned candidate right next to the first.
        var sexualEventIDs = new[]
        {
            "sexual_suggestive_dialogue", "sexual_references", "sexual_nudity",
            "sexual_implied_activity", "sexual_explicit_activity", "sexual_complete_scene",
            "sexual_explicit_vulgar_scene", "sexual_violence", "sexual_kissing"
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
        // Missing sexual_explicit_vulgar_scene here was a real bug: a real Luna-confirmed
        // scene under the current three-category prompt would not even be recognized as a
        // sexual candidate for Terra verification purposes by this filter (see
        // AddUncoveredSexualCandidates' own remarks for the matching bug it shared).
        var sexualEventIDs = new[]
        {
            "sexual_suggestive_dialogue", "sexual_references", "sexual_nudity",
            "sexual_implied_activity", "sexual_explicit_activity", "sexual_complete_scene",
            "sexual_explicit_vulgar_scene", "sexual_violence", "sexual_kissing"
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
                SentenceBoundedContext(
                    item.StartTime, item.EndTime, segments, options.MaximumContextExpansionSeconds),
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
        // Reaches Sol for: Terra's own needsEscalation flag, an accepted scene whose
        // confidence fell short of SolEscalationConfidenceThreshold, or any outright
        // rejection at all -- see NeedsSolReview's remarks for why a rejection is no longer
        // treated as final on a single Terra call, regardless of where the candidate came
        // from. A confident accept is not re-paid-for at Sol -- that does not mean it goes
        // unconfirmed, see NeedsSolReview's remarks.
        var escalationCandidates = terraDecisions
            .Where(item => sourceCandidates.ContainsKey(item.Decision.CandidateKey) &&
                NeedsSolReview(item.Decision, options.SolEscalationConfidenceThreshold))
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

        // Terra's own decision on each escalated candidate is kept as the first of three
        // votes -- see RunSolEscalations' remarks for why a single Sol call fully overriding
        // Terra was exactly the failure mode a real rescan exposed this pipeline to. Grouped
        // rather than keyed directly for the same reason solByCandidate below is: a model
        // returning two decisions for one key must not throw building this lookup.
        var terraDecisionsByKey = terraDecisions
            .GroupBy(item => item.Decision.CandidateKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(item => item.Decision.Confidence).First().Decision,
                StringComparer.Ordinal);
        var solDecisions = await RunSolEscalations(
            escalationCandidates,
            terraDecisionsByKey,
            progress => reportProgress?.Invoke(.5 + progress * .5),
            cancellationToken);
        // Grouped rather than keyed directly. Each request carries one candidate, but the
        // schema permits an array, so a model returning two decisions for the same key threw
        // and failed the job at the very last step, after every model call had been paid for.
        // RunSolEscalations already resolved each key's votes into one final decision, so the
        // most recent one is the majority-resolved answer, not a second competing signal.
        var solByCandidate = solDecisions
            .GroupBy(item => item.CandidateKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Last(),
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
            if (outcome is null)
            {
                // Previously fell back to a lesser label (sexual_implied_activity,
                // sexual_nudity, or sexual_suggestive_dialogue) when a keyword-safety-net
                // seed failed the scene bar, specifically so real content Luna's first pass
                // missed outright was not simply discarded. That fallback no longer has a
                // valid target: under the three-category rule (see ContentTaxonomy.
                // EnforcedLabels' own remarks), there is no lesser sexual label left to
                // report a rejected candidate under at all -- sexual_suggestive_dialogue,
                // sexual_references, sexual_implied_activity, and sexual_explicit_activity
                // are all legacy now. A keyword-net candidate (crude jokes, innuendo, and
                // similar phrasing like "sex doll" that the net's own wide regex still
                // matches on purpose, to give Terra something to review) that Terra/Sol
                // correctly rejects against the new, much stricter explicit_scene bar is a
                // real, correct "this does not qualify" answer now, not a signal to
                // downgrade -- a real production report confirmed the old fallback was
                // still firing after the taxonomy was narrowed, reporting "sex doll"-style
                // crude humor under the no-longer-enforced sexual_suggestive_dialogue label.
                // Standalone nudity (an anatomical word used on its own, with no scene around
                // it at all) is handled entirely separately now and does not go through scene
                // verification in the first place, so it needs nothing recovered here either.
                continue;
            }
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
    /// <remarks>
    /// sexual_kissing joined this set alongside sexual_suggestive_dialogue and
    /// sexual_references: a single kissing moment, on its own, is not a scene candidate and
    /// never becomes one -- the ladder's own sustainedBeyondKissing test is exactly what
    /// keeps kissing from qualifying as sexual_complete_scene by itself. The same cluster and
    /// co-occurrence exceptions below still apply, so a kissing passage that is genuinely
    /// building toward a scene is not excluded.
    /// </remarks>
    private static readonly HashSet<Guid> WeakSexualEventIDs =
        new[] { "sexual_suggestive_dialogue", "sexual_references", "sexual_kissing" }
            .Select(label => ContentTaxonomy.Mappings[label].EventID)
            .ToHashSet();

    /// <summary>
    /// Excludes a lone weak sexual-content mention from reaching Terra at all, unless it is
    /// part of a dense cluster of such mentions or co-occurs with a stronger label.
    /// </summary>
    /// <remarks>
    /// Only a genuinely solitary mention -- exactly one weak <c>suggestive_dialogue</c>,
    /// <c>sexual_references</c>, or <c>sexual_kissing</c> event in the whole candidate set,
    /// with nothing else like it anywhere in the book and nothing stronger nearby -- is
    /// excluded as noise. Two or more weak mentions are each kept, even if a complete
    /// sentence of ordinary narrative separates them into their own clusters: they are still
    /// real, separate instances of exactly the content this rung exists to report, not two
    /// unrelated false alarms. This used to require three or more mentions clustered
    /// together with nothing separating them before any were kept, on the reasoning that two
    /// was still too little to be worth a Terra call -- but a book that stays consistently
    /// subtle (innuendo and flirtation that never escalates into nudity or activity) can
    /// easily have exactly two genuinely distinct, real moments in the same chapter that
    /// never reach three close together, and the old rule suppressed both of them every
    /// time, with no way for that book to ever earn the exemption. At the owner's explicit
    /// request to maximize recall for sexual content, the bar for being "noise" is now a
    /// true singleton only. Any weak mention that shares a passage with a stronger label
    /// (nudity, implied or explicit activity, or an already-flagged complete scene) was
    /// already kept regardless, and still is -- a passing reference next to real activity is
    /// context for that activity, not noise.
    ///
    /// strongEventIDs below still lists the legacy strong labels rather than
    /// sexual_explicit_vulgar_scene, unlike AddUncoveredSexualCandidates and
    /// VerifyCompleteSexualScenes' own matching lists (which needed the real fix -- see their
    /// own remarks). Left as-is here because it is genuinely inert under the current
    /// three-category prompt: this whole method only examines WeakSexualEventIDs
    /// (sexual_suggestive_dialogue, sexual_references, sexual_kissing), none of which Luna
    /// can emit any more, so weakIndices.Length is always 0 and the method returns every
    /// candidate unchanged before strongEventIDs is ever consulted.
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

        // Only a true singleton -- exactly one weak mention anywhere in this whole candidate
        // set, with nothing else like it anywhere else in the book -- is noise. This is
        // deliberately checked across every cluster together, not per cluster: two weak
        // mentions separated by a complete sentence form two separate clusters of one each
        // under the grouping above, but they are still two real, distinct instances of weak
        // sexual content, not two unrelated false alarms, and both are kept. Only when
        // weakIndices.Length itself is 1 is there truly nothing else to corroborate it.
        var excluded = new HashSet<int>();
        if (weakIndices.Length == 1)
        {
            var cluster = clusters[0];

            // A weak mention next to a stronger label anywhere in the same unbroken passage
            // is kept as context for that activity.
            var coOccursWithStrongLabel = ordered.Any(other =>
                strongEventIDs.Contains(other.EventID) &&
                !SeparatedByClearSentence(ordered[cluster[0]], other, segments));
            if (!coOccursWithStrongLabel)
            {
                foreach (var index in cluster) excluded.Add(index);
            }
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
    /// Every rejected candidate also gets Sol's second opinion, regardless of where the
    /// candidate came from. This used to be final for an ordinary Luna-proposed rejection
    /// (neither accepted nor flagged for escalation), on the reasoning that Terra was clear
    /// about it and re-litigating every "no" would multiply cost without changing an outcome
    /// Terra was confident in. That reasoning does not actually hold: this same file's own
    /// evidence (the ACOTAR Part 1 rescan showing the same passage accepted by Terra on one
    /// call and rejected on another, nothing else changed) is about a single call with no
    /// seed/temperature control over sampling variance -- and that variance is a property of
    /// the call, not of how the candidate was sourced. A Luna-proposed candidate's rejection
    /// is exactly as capable of being the unlucky roll as a keyword-safety-net candidate's
    /// is; there was never a real basis for trusting one single Terra "no" more than the
    /// other. At the owner's explicit request to maximize recall for sexual content and
    /// accept the added model cost, every rejection now gets the same one-independent-second-
    /// opinion treatment the safety-net lane already had, rather than reserving it for
    /// candidates that happened to also trip the keyword regex. The parameter that used to
    /// distinguish the two is gone from the signature entirely now that every rejection takes
    /// the same path -- there is no longer a second, narrower condition for it to select
    /// between.
    /// </remarks>
    internal static bool NeedsSolReview(
        VerifiedSceneCandidate decision, double confidenceThreshold) =>
        decision.NeedsEscalation ||
        (decision.Accepted && decision.Confidence < confidenceThreshold) ||
        !decision.Accepted;

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

        // Was a hardcoded "sexual_complete_scene" -- a legacy label since the taxonomy
        // narrowed to three labels (see ContentTaxonomy.EnforcedLabels' own remarks). A
        // real production scan (A Court of Thorns and Roses Part 2's new edition import)
        // confirmed this specific code path had not yet produced a live mislabeled event,
        // only because no candidate on that book happened to clear the new, much stricter
        // explicit-scene bar yet -- but the bug was real and would have silently mislabeled
        // the next accepted scene on any book under a legacy label the app no longer
        // renders a switch for at all. VerifySceneBatch's own prompt was already rewritten
        // for this same bar; this is the other half of the same fix.
        return ("sexual_explicit_vulgar_scene", ContentTaxonomy.Mappings["sexual_explicit_vulgar_scene"]);
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

    /// <summary>
    /// The second and third votes' checkpoint discriminator. The first Sol call for a
    /// candidate keeps using the plain candidate-keyed checkpoint path (so a book rescanned
    /// after this change still reuses that one answer rather than re-paying for it), and this
    /// distinguishes the extra vote's own cached answer from the first.
    /// </summary>
    private const string SecondSolVoteDiscriminator = "sol-vote-2";

    /// <summary>
    /// Escalates each candidate to Sol and combines the result with Terra's own decision as
    /// a genuine three-vote majority, rather than letting a single Sol call fully override
    /// Terra.
    /// </summary>
    /// <remarks>
    /// A real rescan during this fix's own verification found the same passage accepted by
    /// Terra on one independent call and rejected by Sol on another, with nothing else in the
    /// pipeline changed -- OpenAI's Responses API is non-deterministic by default and this
    /// codebase sets no temperature or seed. Letting Sol's single call fully overrule Terra's
    /// meant that variance had the final word every time a candidate escalated, which is
    /// exactly backwards for the cases (needsEscalation, a keyword-safety-net rejection) that
    /// exist specifically because the first opinion was uncertain.
    ///
    /// Requests a second, independent Sol call for every escalated candidate and takes the
    /// majority of Terra's decision plus both Sol calls: accepted only when at least two of
    /// the three votes accept. A 2-1 split still finalizes -- unanimity is not required, since
    /// requiring it would make one dissenting vote enough to silently discard a genuine catch
    /// again, the same failure this exists to fix. The final VerifiedSceneCandidate carries
    /// the fields (startTime/endTime/quote/confidence) from whichever accepting vote has the
    /// highest confidence when accepted, or from the plurality-rejecting vote's own fields
    /// when rejected, so a rejected candidate's own boundary fields are never trusted for
    /// anything -- ResolveSceneOutcome/TryConfirmSceneBoundary only act on an accepted result.
    /// </remarks>
    private async Task<IReadOnlyList<VerifiedSceneCandidate>> RunSolEscalations(
        IReadOnlyList<SolEscalationCandidate> candidates,
        IReadOnlyDictionary<string, VerifiedSceneCandidate> terraDecisionsByKey,
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

                var firstVotePath = SceneVerificationCheckpointPath(
                    batch, SceneEscalationVersion, options.SceneEscalationModel);
                var firstVotePayload = await LoadSceneVerificationCheckpoint(
                    firstVotePath, cancellationToken);
                if (firstVotePayload is null)
                {
                    firstVotePayload = await VerifySceneBatch(
                        batch, options.SceneEscalationModel, cancellationToken);
                    await SaveSceneVerificationCheckpoint(
                        firstVotePath, firstVotePayload, cancellationToken);
                }

                // A second, independent Sol call. Checkpointed separately from the first so
                // the two are never accidentally read as the same cached answer -- that would
                // silently turn a three-vote majority back into a single opinion counted
                // twice, defeating the entire point of this method.
                var secondVotePath = SceneVerificationCheckpointPath(
                    batch, $"{SceneEscalationVersion}-{SecondSolVoteDiscriminator}",
                    options.SceneEscalationModel);
                var secondVotePayload = await LoadSceneVerificationCheckpoint(
                    secondVotePath, cancellationToken);
                if (secondVotePayload is null)
                {
                    secondVotePayload = await VerifySceneBatch(
                        batch, options.SceneEscalationModel, cancellationToken);
                    await SaveSceneVerificationCheckpoint(
                        secondVotePath, secondVotePayload, cancellationToken);
                }

                var finished = Interlocked.Increment(ref completed);
                logger.LogInformation(
                    "Sol escalation progress: {Completed}/{Total} candidate scenes; " +
                    "Terra candidate {TerraCandidateNumber}.",
                    finished, candidates.Count, candidate.TerraIndex + 1);
                reportProgress?.Invoke(finished / (double)candidates.Count);

                var terraVote = terraDecisionsByKey.GetValueOrDefault(candidate.Source.CandidateKey);
                return ResolveMajorityVote(
                    candidate.Source.CandidateKey, terraVote,
                    firstVotePayload.Candidates, secondVotePayload.Candidates);
            }
            finally
            {
                gate.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results.Where(item => item is not null).Select(item => item!).ToArray();
    }

    /// <summary>
    /// Combines Terra's own decision with two independent Sol calls into one majority-
    /// resolved <see cref="VerifiedSceneCandidate"/> for a single escalated candidate.
    /// </summary>
    /// <remarks>
    /// Missing a vote (a model returned no decision for this key at all, rather than a real
    /// reject) does not count toward either side -- only real returned votes are tallied, so
    /// a majority among two remaining votes still resolves normally rather than defaulting to
    /// reject by starvation.
    /// </remarks>
    internal static VerifiedSceneCandidate? ResolveMajorityVote(
        string candidateKey,
        VerifiedSceneCandidate? terraVote,
        IReadOnlyList<VerifiedSceneCandidate> firstSolVoteCandidates,
        IReadOnlyList<VerifiedSceneCandidate> secondSolVoteCandidates)
    {
        VerifiedSceneCandidate? FindVote(IReadOnlyList<VerifiedSceneCandidate> candidates) =>
            candidates.FirstOrDefault(item =>
                string.Equals(item.CandidateKey, candidateKey, StringComparison.Ordinal));

        var votes = new[] { terraVote, FindVote(firstSolVoteCandidates), FindVote(secondSolVoteCandidates) }
            .Where(item => item is not null)
            .Select(item => item!)
            .ToArray();
        if (votes.Length == 0) return null;

        var acceptingVotes = votes.Where(item => item.Accepted).ToArray();
        var rejectingVotes = votes.Where(item => !item.Accepted).ToArray();
        // A tie between two real votes (Terra missing, only both Sol calls returned) keeps
        // whichever side is more confident rather than defaulting either way, since two votes
        // give no true majority to fall back on.
        var accepted = acceptingVotes.Length > rejectingVotes.Length ||
            (acceptingVotes.Length == rejectingVotes.Length && acceptingVotes.Length > 0 &&
                acceptingVotes.Max(item => item.Confidence) >=
                    rejectingVotes.Max(item => item.Confidence));

        // The fields a listener's finalized boundary is built from come from the most
        // confident vote on the winning side only -- never averaged or taken from a
        // dissenting vote, since a boundary this pipeline cannot attribute to one clear
        // supporting call must not reach a listener presented as confirmed.
        var winningSide = accepted ? acceptingVotes : rejectingVotes;
        var chosen = winningSide.Length > 0
            ? winningSide.OrderByDescending(item => item.Confidence).First()
            : votes[0];

        return chosen with
        {
            Accepted = accepted,
            NeedsEscalation = false,
        };
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
                // Flushed on a real gap, exactly as before, but also flushed once the
                // group's own runtime span alone -- before this candidate is even added --
                // already reaches MaximumCoalescedSceneSpanSeconds. Nothing previously
                // bounded how many candidates within mergeGapSeconds of each other could
                // keep joining one group: a long stretch of a real book with sexual-content
                // candidates scattered every 30-40 seconds (well under the 45-second gap)
                // merged into a single ever-growing window with no ceiling at all. A real
                // production job (ACOTAR Part 2) hit exactly this: a scene-verification
                // candidate whose serialized transcript segments alone were large enough
                // that the model's context window could not hold them at any max_tokens
                // this pipeline configures, confirmed directly -- shrinking max_tokens all
                // the way to its own floor still failed with the same overflow. Splitting a
                // long stretch into multiple smaller, still-contiguous verification windows
                // costs one or two extra model calls for an unusually dense passage; letting
                // one grow unbounded costs the whole candidate outright once it exceeds what
                // the model can ever answer, no matter how the retry logic is tuned.
                if (group.Count > 0 &&
                    (candidate.ProposedStartTime > groupEnd + mergeGapSeconds ||
                     candidate.ProposedEndTime - group[0].ProposedStartTime >
                         MaximumCoalescedSceneSpanSeconds))
                {
                    Flush();
                }
                group.Add(candidate);
                groupEnd = Math.Max(groupEnd, candidate.ProposedEndTime);
            }
            Flush();
        }
        return result;
    }

    /// <summary>
    /// The longest runtime span one coalesced scene-verification candidate may cover before
    /// it is split into a new group instead of continuing to grow. See
    /// <see cref="CoalesceSceneCandidates"/>'s own remarks for the real production failure
    /// this exists to prevent: a candidate with no ceiling at all can accumulate enough
    /// transcript segments that its serialized size alone exceeds what the model's context
    /// window can hold at any output budget. 20 minutes is comfortably longer than any
    /// single real scene this pipeline's own examples describe, while still bounding how
    /// large one request's transcript payload can grow regardless of how densely Luna's
    /// first pass proposes candidates through a given stretch of a book.
    /// </summary>
    private const double MaximumCoalescedSceneSpanSeconds = 1200;

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
                // Diagnostic left in place, not just for the incident that prompted it: a
                // real production candidate (ACOTAR Part 2) reported the exact same
                // context-length overflow, with the exact same claimed input-token count,
                // on every retry against three different things that should have mattered --
                // a smaller max_tokens, a longer wait, and a fully restarted vLLM process --
                // while its own real content was an ordinary 13,512-character passage with
                // no unusual characters, nowhere close to large enough to explain the
                // claimed figure. That rules out a genuinely oversized prompt, a transient
                // queue, and a stale/corrupted server-side cache as the cause; what remains
                // is a real vLLM bug tied to this exact request's content that this
                // pipeline cannot diagnose or fix from the client side tonight. Logged
                // unconditionally so a repeat is identifiable without another blind retry.
                var totalSegments = batch.Sum(item => item.Segments.Count);
                var approximateCharacters = batch.Sum(item =>
                    item.Segments.Sum(segment => segment.Text.Length));
                logger.LogInformation(
                    "Scene verification batch {BatchNumber}: {CandidateCount} candidate(s), " +
                    "{TotalSegments} total transcript segments, ~{ApproximateCharacters} " +
                    "characters, span {SpanStart:F0}s-{SpanEnd:F0}s.",
                    index + 1, batch.Count, totalSegments, approximateCharacters,
                    batch.Min(item => item.ProposedStartTime),
                    batch.Max(item => item.ProposedEndTime));
                try
                {
                    payload = await VerifySceneBatch(batch, model, cancellationToken);
                }
                catch (HttpRequestException error) when (
                    error.Message.Contains("context-overflow retries") ||
                    error.Message.Contains("minimum viable max_tokens"))
                {
                    // A real, reproducible vLLM-side failure (see the remarks above) must
                    // not fail the entire scan job over one candidate this pipeline has
                    // already given every reasonable chance to succeed. An unverified
                    // candidate is dropped rather than reported: Terra's confirmation is
                    // what lets a proposed scene reach a listener as a filter event at all
                    // (see VerifyCompleteSexualScenes' own remarks), so a candidate that
                    // never got a real verification answer must be treated the same as one
                    // Terra rejected, not smuggled through as accepted.
                    logger.LogError(
                        error,
                        "Scene verification batch {BatchNumber} could not be completed " +
                        "after exhausting every retry; this candidate is dropped rather " +
                        "than failing the whole scan job.",
                        index + 1);
                    payload = new SceneVerificationPayload([]);
                }
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
For an accepted candidate, refine startTime to the beginning of the continuous romantic or
physical escalation that leads directly into the act -- kissing, embracing, undressing, or
touching that builds without a break in the scene -- not only the single sentence containing
the act's own unmistakable moment. A listener who wants this scene skipped is asking to miss
the buildup that makes the scene recognizable as one, not to have the skip begin partway
through it once the act itself is unambiguous. Only start later, at the act itself, when the
scene truly opens there with no preceding kissing or touching that belongs to the same
continuous moment. Set endTime where that activity clearly finishes.
The escalation that sets startTime is physical. Kissing, embracing, touching, undressing: one of
those marks where the scene begins. Dialogue does not, however intimate, charged, or clearly
leading somewhere it is -- not flirting, not a confession, not negotiating or asking for consent,
not a conversation about a past encounter or about trust, and not narration of what a character
wants or feels. Those may sit immediately before the scene and may be what makes it obvious one is
coming, and they are still not part of it. Start at the first physical contact that belongs to the
unbroken escalation, not at the talking that preceded it.
Example of a startTime placed too early -- the opposite mistake to the one below, and equally
wrong: a couple spend two minutes discussing her past and whether she trusts him, after which he
undresses and the encounter begins. The correct startTime is the undressing. A startTime back in
the conversation sweeps up minutes of dialogue that contains nothing a listener asked to skip, and
a skip that removes ordinary conversation teaches them the filter cannot be trusted with the book.

Example of a correct startTime -- a real passage from a production book, reported by a
listener because the skip began too late and audible content still played:
"He leaned forward and kissed me lightly. Not forever. And though I knew it was a lie, I put my
arms around his neck and kissed him. He pulled me onto his lap, holding me tightly against him
as his lips parted mine. I became aware of every pore in my body when his tongue entered my
mouth. [...] I pushed Tamlin onto the bed, straddling him."
The correct startTime is at "He leaned forward and kissed me lightly" -- the kissing is the
scene's own beginning, part of the same unbroken escalation, not separate lead-in to be left
playing. A startTime placed instead at "I pushed Tamlin onto the bed" (the act's own most
unambiguous sentence) is wrong: it skips too little, and is exactly the mistake this
instruction exists to prevent.

Example of a correct rejection -- ordinary romantic tension with no escalation to skip:
"He caught her eye across the room and she felt her pulse quicken. There was something about
the way he looked at her." This is attraction and narration, not activity; accepted must be
false here regardless of how romantically charged the prose is, because there is no kissing,
touching, or undressing to mark where a scene even begins.

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
Act as a strict final verifier for one audiobook explicit-scene skip range. Each candidate was
produced by a high-recall detector and may be a false positive. This deployment's bar for this
label is deliberately high and narrow, permanently loosened from a much broader prior rule at
the owner's explicit request.

Set directSexualActEvidence=true ONLY when this candidate's own transcript directly, explicitly
describes a sexual act in graphic, mechanical, anatomical detail -- for example an explicit
description of penetration, or similarly vulgar, graphic description of a sexual act's physical
mechanics using explicit, literal (not euphemistic) anatomical or sexual-act vocabulary. Set
sustainedBeyondKissing=true the same way: true only when the passage reaches that same graphic,
explicit bar, not merely "beyond kissing into any sexual act."

REJECT (accepted must be false) all of the following, even when clearly present and even at
length: romantic or sexual tension; kissing; grinding; biting; desire stated in prose (wanting
someone, wanting to be touched, wanting someone's body); an intimate encounter conveyed through
emotion and physical sensation rather than explicit mechanics; a euphemism for sex, however
obvious ("stolen hours together," "tangled in the sheets," "rutting," "lost ourselves in each
other"); nudity alone; attraction; sexual jokes or references; or intense, forceful physicality
read as mutual escalation (biting that pins rather than injures, growling, gripping, a partner
pushing the other away mid-encounter and then pressing back into contact). None of that is
evidence for this label, however long the passage dwells on it or however clearly a sexual
encounter is happening -- only explicit, graphic, mechanical description crosses this bar. This
lane is for consensual activity only: if the passage instead shows the act was non-consensual,
reject it here (accepted=false) rather than reclassifying it -- a separate sexual-violence
review handles that case.

Example -- a real passage from a production book that must be REJECTED despite real physical
intensity and an unambiguous sexual encounter underway: "He grabbed my hands again, and bit my
neck. His teeth clamped onto the tender spot where my neck met my shoulder. I couldn't move,
couldn't think, and my world narrowed to the feeling of his lips and teeth against my skin. The
push of his body against mine, the hard and the soft, made me see red, see lightning, made me
grind my hips against his." This passage has real desire, real forceful physicality, and reads
unambiguously as a sexual encounter in progress -- and must still be rejected, because nothing
in it is graphic, mechanical, or anatomically explicit. Biting, grinding, and desire stated in
prose are all on the allowed side of this deployment's new rule, not evidence for this label.

Example -- a real passage that must be ACCEPTED: an extended description of oral sex using
explicit anatomical vocabulary for genitalia, narrating the physical mechanics of the act in
graphic detail (size, sensation, movement, taste) rather than conveying it through emotion or
euphemism. The deciding difference from the rejected example above is not length, intensity, or
whether a sexual act is clearly happening -- both examples have that -- it is whether the
passage actually uses explicit, literal, mechanical description or stays in emotion/sensation/
euphemism throughout.

accepted may be true only when BOTH evidence booleans are true and confidence is at least 0.85.
Otherwise accepted must be false. When genuinely unsure whether a passage has crossed into
graphic/mechanical territory or is still on the allowed side (tension, non-graphic intimacy,
euphemism), resolve it as allowed and set accepted=false -- this label exists for a narrow, high
bar, not a broad one, and the new rule explicitly keeps far more content playable than before.
Set needsEscalation=true only when the candidate is still a plausible explicit scene but the
evidence, confidence, or exact boundaries are uncertain and require a stronger final review. Set
needsEscalation=false for clear rejections, including any passage whose only content is tension,
non-graphic intimacy, kissing, or nudity alone. Confirmed accepted scenes will also receive
final review. Confidence must describe the evidence that the passage is explicit and graphic,
not merely that a sexual act is occurring.
""") + SceneVerificationClosingInstructions + """

Candidates:
""" + JsonSerializer.Serialize(candidates.Select(ToPromptCandidate));
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
        // Strips per-word timing before the prompt is built, the same real bug and fix as
        // ToPromptCandidate's own remarks: TranscriptSegment's Words property (full per-word
        // text and timing) is never read by this first pass, only by the later
        // TranscriptWordLocator.FindPhrase call against the original in-memory segments, yet
        // it was being serialized into the model's prompt on every single first-pass batch
        // here too -- not just Terra/Sol's verification calls. This is the more significant
        // instance of the same bug: every one of Luna's batches was carrying this cost, on
        // every book, on both the Bedrock and self-hosted transports, for the lifetime of
        // this pipeline.
        var transcript = JsonSerializer.Serialize(
            segments.Select(segment => segment with { Words = null }));

        return """
Act as an audiobook content-preference classifier. Identify only events that are explicitly
supported by the supplied transcript. This deployment filters sexual content only -- profanity,
violence, substance use, blasphemy, and self-harm are not in scope and must never be reported,
no matter how prominent they are in a passage.

For isolated events, return the narrowest supported timestamps. A short reference is a short
event: if three words carry it, the range should cover those three words and not the sentence
or paragraph around them. Never widen a brief event to be safe -- a wide range on a passing
reference removes narration the listener wanted to hear.

This deployment looks for exactly three things, permanently narrowed down from a much larger
set of sexual-content rungs at the owner's explicit request. Ordinary romantic or sexual
tension, kissing, flirtation, innuendo, crude jokes, suggestive dialogue, nudity mentioned only
in passing with no anatomical word, and non-graphic sexual activity (including a full scene
described without graphic mechanical detail) are all explicitly ALLOWED now and must never be
reported under any label. Only the three things below qualify at all.

sexual_nudity -- ONLY a specific anatomical word or its direct synonym naming intimate anatomy:
genitalia (penis, vagina, cock, dick as anatomy, pussy, genitals), breasts, nipples, areolas, or
buttocks (butt, ass as a body part). Report the narrowest possible range -- the exact word or
short phrase that uses the anatomical term, never the sentence or paragraph around it. A general
reference to someone being naked, nude, unclothed, undressed, bare, or removing clothing does
NOT qualify on its own -- this label is only for the literal anatomical word itself, not for
nakedness in general. A word in this list used in a non-body sense (an insult, a euphemism with
no anatomical meaning, an unrelated word that happens to share a spelling) does not qualify
either -- judge the word's actual use in context, not just its presence. No word is sexual
content by itself: a hand on a chest during first aid, a breast wound in battle, a medical
examination, a badge pinned at her breast, or armour such as a breastplate is anatomy mentioned
in a non-sexual, non-exposed context and does not qualify. The word qualifies when the passage
itself describes that anatomy as bare, uncovered, exposed, or otherwise names it directly in a
body-revealing or sexual context.

sexual_explicit_vulgar_scene -- ONLY a passage that crosses into graphic, mechanical, anatomical
description of a sexual act -- for example an explicit description of penetration, or similarly
vulgar, graphic description of a sexual act's physical mechanics using explicit, literal (not
euphemistic) anatomical or sexual-act vocabulary. This is a genuinely high bar, not merely "a
sex scene happened." Do NOT report: romantic or sexual tension; kissing; grinding; biting;
desire stated in prose (wanting someone, wanting to be touched, wanting someone's body); an
embrace or an intimate scene described through emotion and sensation rather than mechanics; a
euphemism for sex, even an obvious one ("stolen hours together," "tangled in the sheets,"
"rutting," "lost ourselves in each other"); or intense, forceful physicality read as mutual
escalation (biting that pins rather than injures, growling, gripping, a push followed by the
same character pressing back into contact). ALL of that is explicitly ALLOWED and must never be
flagged under this label -- it is a real, permanent loosening, not an oversight. Only flag a
passage once it actually, unmistakably crosses into graphic, mechanically descriptive sexual
content: explicit, literal anatomical vocabulary used in the act itself, an explicit description
of penetration or other sexual acts' physical mechanics, or graphic physiological detail
(sensation, taste, wetness) described explicitly during the act, not merely alluded to. Report
the full span of the explicit material, from where it becomes explicit to where it stops, not
just one sentence of it. When genuinely unsure whether a passage has crossed this bar or is
still on the allowed side (tension, non-graphic intimacy), resolve it as allowed and do not
report it -- this label exists for a narrow, high bar, not a broad one.

sexual_violence -- ONLY an ongoing scene depicting rape or sexual assault as a sexual act, as it
happens -- it must itself be sexual in nature, not merely violent. This is strictly separate
from ordinary physical violence: a beating, an attack, a fight, combat, or any other assault
with no sexual element, however brutal, does NOT qualify here and must never be reported under
this label -- ordinary violence is completely out of scope for this filter and must never be
reported under any label at all. A brief reference or backstory mention of sexual violence (a
character stating or recalling that they were assaulted, without the act itself being depicted
in this passage) also does NOT qualify -- only a scene actually depicting the act, as it is
happening, qualifies. Judge consent from what the passage itself establishes -- refusal,
resistance, incapacitation, coercion, or an imbalance of power exploited to force the act -- not
from a character's profession, relationship, or the setting alone. A passage ambiguous about
consent is not sexual_violence.

Allowed labels:
""" + AllowedLabelList + """
For safeDescription, write a neutral, discreet, non-graphic summary of at most 80 characters.
It must still tell a listener why they might choose to skip: name the high-level situation, not
the mechanics, and never name the specific anatomical word found. For example: "A character
removes clothing and is described without clothing", "A sexual scene is described in explicit,
graphic detail", or "A non-consensual sexual encounter is described". Never name intimate
anatomy or describe touching, squeezing, positions, or mechanics. Do not use vague filler such
as "related sexual content", "continuous passage", or "sexual activity described".
The description itself must not expose a listener to the unwanted explicit material they are
trying to avoid.
For every event, also return quote: the exact consecutive words from the supplied transcript
text that support this event, copied verbatim from a single segment's text field, not
paraphrased, summarized, or combined across segments. This is not shown to any listener; it is
how the server locates the words you mean. A short event's quote should be just the words that
carry it, matching startTime and endTime -- the anatomical word itself for a nudity event, not
the whole sentence around it. A longer scene's quote should be the clearest single supporting
sentence or clause inside it, near where you believe the scene most clearly qualifies; it does
not need to span the whole scene, and the server uses startTime and endTime for the outer
boundary. If you cannot point to specific words that justify an event, do not report the event.
Do not invent content or identifiers. Omit events below 0.6 confidence -- this deployment's
three remaining labels are a genuinely high bar each, and a low-confidence guess at any of them
costs a listener real narration they wanted to hear.

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
                        // No maxLength here on purpose -- see SceneVerificationResponseSchema's
                        // own remarks for why a length-bounded string in vLLM's guided-decoding
                        // schema is a real production bug, not a safety net. SafeDescription
                        // already truncates to 80 characters server-side before anything is
                        // trusted or shown to a listener.
                        ["safeDescription"] = new JsonObject { ["type"] = "string" },
                        ["profanityWord"] = new JsonObject
                        {
                            ["type"] = new JsonArray("string", "null")
                        },
                        // Never shown to a listener. This is how AddEvent locates the exact
                        // words that justify the event in the transcript's own word timing,
                        // rather than trusting startTime/endTime as invented numbers.
                        ["quote"] = new JsonObject { ["type"] = "string" }
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
                        // No maxLength here, deliberately. Real production evidence traced
                        // to this exact constraint: vLLM's guided-decoding backend
                        // (xgrammar) silently drops a JSON Schema string's maxLength bound --
                        // a documented, known vLLM limitation, not something specific to this
                        // prompt or candidate -- so the model received no actual signal to
                        // stop at 80 characters. It kept generating past the schema's own
                        // limit with nothing to close the string or the surrounding object
                        // correctly, corrupting the JSON at (or very near) the 80th
                        // character on every attempt, deterministically, because the failure
                        // is structural rather than a sampling accident: retrying an
                        // unconstrained-in-practice schema against the same over-length
                        // answer reproduces the same broken close every time. Confirmed by
                        // an identical failure recurring across two different real
                        // candidates, both truncating a safeDescription at exactly 80
                        // characters. SafeDescription already truncates to 80 characters
                        // server-side before anything is trusted or shown to a listener, so
                        // the schema constraint was never doing safety work the code did not
                        // already do -- only breaking generation for a description whose
                        // natural length happened to land past 80.
                        ["safeDescription"] = new JsonObject { ["type"] = "string" },
                        // Never shown to a listener. Confirms the refined startTime against
                        // the transcript's own word timing before it is trusted.
                        ["quote"] = new JsonObject { ["type"] = "string" }
                    }
                }
            }
        }
    };

    internal sealed record AnalysisPayload(
        [property: JsonPropertyName("events")]
        IReadOnlyList<ClassifiedEvent> Events);

    private sealed record ContentBatchResult(
        int Index,
        IReadOnlyList<TranscriptSegment> Batch,
        AnalysisPayload Payload);

    internal sealed record ClassifiedEvent(
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

    /// <summary>
    /// Strips per-word timing before a candidate is serialized into the model prompt.
    /// </summary>
    /// <remarks>
    /// A real production failure (Carl's Doomsday Scenario, repeatedly, on Bedrock's
    /// 32,768-token Qwen3-32B) traced back to this: <see cref="SceneVerificationCandidate.Segments"/>
    /// is <see cref="TranscriptSegment"/>, whose own Words property (per-word text and timing,
    /// one object per word) was serialized into the prompt along with everything else, even
    /// though Terra never reads Words -- only the later TranscriptWordLocator.FindPhrase call
    /// does, against the original in-memory segments, never against the prompt text. The
    /// per-batch diagnostic log here only ever measured segment.Text.Length, which stayed
    /// small and modest on every failing candidate, while the real serialized payload sent to
    /// the model silently carried a full per-word object for every word in every segment on
    /// top of it -- large enough on a dialogue-dense passage to be the actual cause of an
    /// overflow that looked, from the measured numbers alone, like it should not have been
    /// possible. This was never caught on the self-hosted vLLM lane because that model's
    /// 65,536-token context (2x Qwen3-32B's) had enough headroom to absorb the same bloat
    /// without ever reaching its own ceiling.
    /// </remarks>
    private static SceneVerificationCandidate ToPromptCandidate(SceneVerificationCandidate candidate) =>
        candidate with
        {
            Segments = candidate.Segments
                .Select(segment => segment with { Words = null })
                .ToList()
        };

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
