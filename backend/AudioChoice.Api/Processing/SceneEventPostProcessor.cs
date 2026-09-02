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
    private const double MergeGapSeconds = 45;
    /// <summary>
    /// Seconds added either side of a merged scene, so a skip does not clip its own edges.
    ///
    /// Reduced from eight to three. Eight was chosen to be safe and cost sixteen seconds on
    /// every scene, which on a book with forty of them is more than ten minutes of audio
    /// removed for margin alone. Three still covers the case this exists for -- a transcript
    /// boundary landing a word or two early -- without turning caution into the largest single
    /// contributor to how much of a book disappears.
    ///
    /// Applies only to complete scenes. A short event keeps the narrowest bounds the model
    /// supported, because padding a three-word phrase is how a phrase becomes a passage.
    /// </summary>
    private const double SafetyPaddingSeconds = 3;

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
    /// Lowered rather than removed. Half a minute of narration is still more likely one
    /// explicit sentence and its surroundings than a scene, and the narrower
    /// explicit-activity events continue to cover those moments regardless.
    /// </remarks>
    private const double MinimumCompleteSceneSeconds = 30;

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
            if (candidate.StartTime <= clusterEnd + MergeGapSeconds)
            {
                cluster.Add(candidate);
                continue;
            }

            mergedScenes.Add(Merge(cluster, completeScene, audiobookStart, audiobookEnd));
            cluster = [candidate];
        }

        mergedScenes.Add(Merge(cluster, completeScene, audiobookStart, audiobookEnd));

        // Complete-scene events drive broad automatic skips. Anything shorter remains
        // represented by the separately detected explicit/implied activity events, but
        // is too narrow to justify expanding into a scene-level skip.
        mergedScenes = mergedScenes
            // Padding must not promote a short isolated phrase into a complete-scene
            // skip. Require the unpadded supported range to meet the minimum.
            .Where(item => item.EndTime - item.StartTime >=
                MinimumCompleteSceneSeconds + (SafetyPaddingSeconds * 2))
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
        double audiobookEnd)
    {
        var start = Math.Max(audiobookStart, cluster.Min(item => item.StartTime) - SafetyPaddingSeconds);
        var end = Math.Min(audiobookEnd, cluster.Max(item => item.EndTime) + SafetyPaddingSeconds);
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
