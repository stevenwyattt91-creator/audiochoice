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

    public static IReadOnlyList<ScanEvent> Process(
        IReadOnlyList<ScanEvent> events,
        IReadOnlyList<TranscriptSegment> segments)
    {
        var completeScene = ContentTaxonomy.Mappings["sexual_complete_scene"];
        var sceneEvents = events
            .Where(item => item.EventID == completeScene.EventID)
            .OrderBy(item => item.StartTime)
            .ToArray();

        if (sceneEvents.Length == 0) return events;

        var audiobookStart = segments.Count == 0 ? 0 : segments.Min(item => item.StartTime);
        var audiobookEnd = segments.Count == 0
            ? sceneEvents.Max(item => item.EndTime)
            : segments.Max(item => item.EndTime);
        var mergedScenes = new List<ScanEvent>();
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

            mergedScenes.Add(Merge(cluster, completeScene, audiobookStart, audiobookEnd, segments));
            cluster = [candidate];
        }

        mergedScenes.Add(Merge(cluster, completeScene, audiobookStart, audiobookEnd, segments));

        // Complete-scene events drive broad automatic skips. Anything shorter remains
        // represented by the separately detected explicit/implied activity events, but
        // is too narrow to justify expanding into a scene-level skip.
        mergedScenes = mergedScenes
            .Where(item => item.EndTime - item.StartTime >= MinimumCompleteSceneSeconds)
            .ToList();

        return events
            .Where(item => item.EventID != completeScene.EventID)
            .Concat(mergedScenes)
            .OrderBy(item => item.StartTime)
            .ToArray();
    }

    private static ScanEvent Merge(
        IReadOnlyList<ScanEvent> cluster,
        TaxonomyMapping mapping,
        double audiobookStart,
        double audiobookEnd,
        IReadOnlyList<TranscriptSegment> segments)
    {
        var rawStart = cluster.Min(item => item.StartTime);
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
}
