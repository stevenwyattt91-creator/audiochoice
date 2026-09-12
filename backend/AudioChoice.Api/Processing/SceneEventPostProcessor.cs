using System.Security.Cryptography;
using System.Text;
using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Processing;

/// <summary>
/// Joins scene ranges independently reported by overlapping model requests. This is
/// deliberately limited to complete sexual scenes; short references and dialogue must
/// remain individually controllable and must not grow into multi-minute skips.
/// </summary>
public static class SceneEventPostProcessor
{
    /// <summary>
    /// The shortest merged range still worth a scene-level skip.
    /// </summary>
    /// <remarks>
    /// This was 60 seconds, set when the first pass proposed scenes on its own and the
    /// threshold was the only thing stopping a single explicit sentence becoming a
    /// minute-long skip. Every scene now clears Terra and Sol at 0.85 confidence first, so
    /// the threshold is guarding against far less than it used to while still discarding
    /// genuine short scenes: a verified forty-second encounter kept no scene skip at all.
    ///
    /// Lowered again, to fifteen seconds, because thirty was removing real scenes. A tester
    /// listening with only Complete sex scenes enabled heard two scenes play in one book: brief
    /// encounters that were detected, verified, and then dropped for being short. Someone who
    /// switches that on is asking for sex scenes to be gone, and the short ones are precisely the
    /// ones a length rule discards.
    ///
    /// Kept rather than removed. A floor still stops a single explicit sentence and its padding
    /// from becoming a scene-sized skip, and every range reaching here has already cleared two
    /// verification passes at 0.85 confidence. Measured against the cluster's own raw span,
    /// before word-snapping: snapping moves an edge by at most the distance to the nearest real
    /// word, never by the multi-second amount a flat pad used to add, so there is nothing left
    /// to double-count here the way the old floor had to account for its own padding.
    /// </remarks>
    private const double MinimumCompleteSceneSeconds = 15;

    /// <summary>
    /// The scene-level event types this processor clusters, merges, and word-snaps.
    /// </summary>
    /// <remarks>
    /// sexual_violence is treated exactly like sexual_complete_scene structurally -- same
    /// sentence-boundary merge rule, same word-snap, same minimum-length floor -- but the two
    /// are always clustered and merged separately from each other, never together. Terra/Sol
    /// already keep the two labels as mutually exclusive claims about the same passage (see
    /// OpenAIContentAnalysisProvider's per-lane verification), and merging a confirmed
    /// assault scene with a nearby confirmed consensual one here would erase that distinction
    /// right before it reaches the listener.
    /// </remarks>
    private static readonly string[] SceneLabels = ["sexual_complete_scene", "sexual_violence"];

    /// <summary>
    /// The individual sexual-content labels below sexual_complete_scene on the ladder, whose
    /// own continuous chain may extend a confirmed scene's start backward.
    /// </summary>
    /// <remarks>
    /// A real production gap this exists to close: the analysis batches a book in
    /// overlapping windows, so a scene's real buildup and its eventual confirmed act can each
    /// be seen by a different window. The earlier window, seeing only the buildup with no act
    /// yet in view, correctly stays on the individual ladder rungs (a kiss, an undressing) and
    /// never itself proposes sexual_complete_scene -- the taxonomy's own ladder instructions
    /// forbid that. The later, overlapping window sees the act and correctly proposes
    /// sexual_complete_scene, but anchored to where ITS OWN window began, not to the true
    /// earlier point the first window already saw. Neither window's individual judgment was
    /// wrong; nothing previously reconnected their two partial views of one continuous scene.
    /// This is that reconnection: it does not invent a start time, it only recognizes that an
    /// unbroken run of the first window's own individually-labeled events immediately
    /// preceding a confirmed scene IS that scene's real, earlier beginning.
    /// </remarks>
    private static readonly string[] LowerRungSexualLabels =
    [
        "sexual_suggestive_dialogue", "sexual_references", "sexual_nudity",
        "sexual_implied_activity", "sexual_explicit_activity"
    ];

    public static IReadOnlyList<ScanEvent> Process(
        IReadOnlyList<ScanEvent> events,
        IReadOnlyList<TranscriptSegment> segments)
    {
        var sceneMappings = SceneLabels.Select(label => ContentTaxonomy.Mappings[label]).ToArray();
        var sceneEventIDs = sceneMappings.Select(item => item.EventID).ToHashSet();
        var lowerRungEventIDs = LowerRungSexualLabels
            .Select(label => ContentTaxonomy.Mappings[label].EventID)
            .ToHashSet();
        var lowerRungEvents = events
            .Where(item => lowerRungEventIDs.Contains(item.EventID))
            .OrderBy(item => item.StartTime)
            .ToArray();

        var audiobookStart = segments.Count == 0 ? 0 : segments.Min(item => item.StartTime);
        var mergedScenes = new List<ScanEvent>();
        var anySceneEvents = false;

        foreach (var mapping in sceneMappings)
        {
            var sceneEvents = events
                .Where(item => item.EventID == mapping.EventID)
                .OrderBy(item => item.StartTime)
                .ToArray();
            if (sceneEvents.Length == 0) continue;
            anySceneEvents = true;

            var audiobookEnd = segments.Count == 0
                ? sceneEvents.Max(item => item.EndTime)
                : segments.Max(item => item.EndTime);
            var cluster = new List<ScanEvent> { sceneEvents[0] };

            foreach (var candidate in sceneEvents.Skip(1))
            {
                var clusterEnd = cluster.Max(item => item.EndTime);
                if (!TranscriptSentenceBoundaries.HasClearSentenceBetween(
                    clusterEnd, candidate.StartTime, segments))
                {
                    cluster.Add(candidate);
                    continue;
                }

                mergedScenes.Add(Merge(
                    cluster, mapping, audiobookStart, audiobookEnd, segments, lowerRungEvents));
                cluster = [candidate];
            }

            mergedScenes.Add(Merge(
                cluster, mapping, audiobookStart, audiobookEnd, segments, lowerRungEvents));
        }

        if (!anySceneEvents) return events;

        // Scene-level events drive broad automatic skips. Anything shorter remains
        // represented by the separately detected explicit/implied activity events, but
        // is too narrow to justify expanding into a scene-level skip.
        mergedScenes = mergedScenes
            .Where(item => item.EndTime - item.StartTime >= MinimumCompleteSceneSeconds)
            .ToList();

        return events
            .Where(item => !sceneEventIDs.Contains(item.EventID))
            .Concat(mergedScenes)
            .OrderBy(item => item.StartTime)
            .ToArray();
    }

    private static ScanEvent Merge(
        IReadOnlyList<ScanEvent> cluster,
        TaxonomyMapping mapping,
        double audiobookStart,
        double audiobookEnd,
        IReadOnlyList<TranscriptSegment> segments,
        IReadOnlyList<ScanEvent> lowerRungEvents)
    {
        var rawStart = ExtendStartThroughLowerRungChain(
            cluster.Min(item => item.StartTime), lowerRungEvents, segments);
        var rawEnd = cluster.Max(item => item.EndTime);

        // Snapped to the transcript's own nearest word instead of padded by a flat number of
        // seconds either side. A flat pad was a guess at how far a transcript boundary could
        // land from the words it was supposed to bound; the transcript already states where
        // those words actually are, so the guess is replaced with that fact. Falls back to the
        // raw cluster range when no supplied segment carries word timing at all -- the same
        // fallback every other word-snapped boundary in this pipeline uses for a transcript
        // saved before word timings existed.
        var snapped = TranscriptWordLocator.SnapToNearestWord(segments, rawStart, rawEnd);
        var start = Math.Max(audiobookStart, snapped?.Start ?? rawStart);
        var end = Math.Min(audiobookEnd, snapped?.End ?? rawEnd);
        var confidence = cluster.Max(item => item.Confidence);
        var description = cluster
            .Select(item => item.SafeDescription?.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item) &&
                !string.Equals(item, "Complete sexual scene", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(item, "Content event detected", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item!.Length)
            .FirstOrDefault()
            ?? "Sustained sexual activity";
        var material = $"scene|{mapping.EventID:N}|{start:F1}|{end:F1}";
        var stableKey = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();

        return new ScanEvent(
            Guid.NewGuid(), start, end, mapping.CategoryID, mapping.GroupID,
            mapping.EventID, confidence, stableKey, description);
    }

    /// <summary>
    /// The largest silence, in seconds, between one lower-rung event's end and the next
    /// (moving backward from a confirmed scene) that still counts as the same unbroken
    /// buildup rather than an unrelated, merely nearby moment.
    /// </summary>
    /// <remarks>
    /// Deliberately a time gap, not <see cref="TranscriptSentenceBoundaries.HasClearSentenceBetween"/>
    /// -- that check is right for deciding whether two WIDE scene candidates are one
    /// continuous passage, where a real topic change is many sentences away. It is the wrong
    /// test here: each individual lower-rung event is typically one sentence on its own, so
    /// of course an ordinary sentence terminator sits between two adjacent ones -- that is
    /// just normal narration between one beat and the next within the SAME continuous scene
    /// ("I didn't want apologies. Didn't want sympathy or coddling."), not evidence of a
    /// break. A real production gap this fix closes measured 4.6-6.7 seconds between
    /// consecutive buildup beats; this cap is set with real margin above that, well short of
    /// a length that could plausibly bridge two genuinely separate moments of tension
    /// scattered elsewhere in the same chapter.
    /// </remarks>
    private const double MaximumLowerRungChainGapSeconds = 20;

    /// <summary>
    /// Walks backward through <paramref name="lowerRungEvents"/> from
    /// <paramref name="sceneStart"/>, extending the start earlier through any unbroken run of
    /// individually-labeled sexual events immediately preceding the scene -- the earlier
    /// analysis window's own view of this exact scene's real buildup, which never itself
    /// became a sexual_complete_scene event because that window's own view ended before the
    /// act it eventually leads into.
    /// </summary>
    /// <remarks>
    /// This deliberately walks the model's own already-labeled events rather than
    /// re-scanning raw transcript text, so this never extends a scene into narration no
    /// detector ever flagged as sexual content at all -- it only recognizes that several
    /// partial views the pipeline already trusted individually describe one continuous
    /// moment.
    /// </remarks>
    private static double ExtendStartThroughLowerRungChain(
        double sceneStart,
        IReadOnlyList<ScanEvent> lowerRungEvents,
        IReadOnlyList<TranscriptSegment> segments)
    {
        var earliestStart = sceneStart;
        var candidates = lowerRungEvents
            .Where(item => item.StartTime < sceneStart)
            .OrderByDescending(item => item.StartTime)
            .ToArray();

        var chainEnd = sceneStart;
        foreach (var candidate in candidates)
        {
            if (candidate.StartTime >= chainEnd) continue;
            if (chainEnd - candidate.EndTime > MaximumLowerRungChainGapSeconds) break;
            earliestStart = Math.Min(earliestStart, candidate.StartTime);
            chainEnd = candidate.StartTime;
        }

        return earliestStart;
    }
}
