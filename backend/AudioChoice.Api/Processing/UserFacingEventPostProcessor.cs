using System.Security.Cryptography;
using System.Text;
using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Processing;

/// <summary>
/// Reduces the number of controls presented to listeners without discarding the
/// underlying detections or their playback ranges.
/// </summary>
public static class UserFacingEventPostProcessor
{
    private const double NearbyEventGapSeconds = 5;

    public static IReadOnlyList<ScanEvent> Process(IReadOnlyList<ScanEvent> events)
    {
        var alcoholIDs = new[] { "substance_alcohol_use", "substance_intoxication" }
            .Select(label => ContentTaxonomy.Mappings[label].EventID).ToHashSet();
        var drugIDs = new[]
        {
            "substance_drug_reference", "substance_drug_use", "substance_abuse_overdose"
        }.Select(label => ContentTaxonomy.Mappings[label].EventID).ToHashSet();
        var alcoholGroup = ContentTaxonomy.Mappings["substance_alcohol_use"].GroupID;
        var drugGroup = ContentTaxonomy.Mappings["substance_drug_use"].GroupID;
        var alcoholKey = Hash("control|substance|alcohol");
        var drugKey = Hash("control|substance|drugs");

        var normalized = events.Select(item =>
            alcoholIDs.Contains(item.EventID)
                ? item with
                {
                    GroupID = alcoholGroup,
                    AggregateKey = alcoholKey,
                    AggregateDisplay = "Alcohol use"
                }
                : drugIDs.Contains(item.EventID)
                    ? item with
                    {
                        GroupID = drugGroup,
                        AggregateKey = drugKey,
                        AggregateDisplay = "Drug use"
                    }
                    : item).ToArray();

        var profanityCategory = ContentTaxonomy.Mappings["profanity_mild"].CategoryID;
        var substanceCategory = ContentTaxonomy.Mappings["substance_alcohol_use"].CategoryID;
        var replacements = new Dictionary<Guid, ScanEvent>();
        foreach (var group in normalized
                     .Where(item => item.CategoryID != profanityCategory &&
                                    item.CategoryID != substanceCategory)
                     .GroupBy(item => item.CategoryID))
        {
            var ordered = group.OrderBy(item => item.StartTime).ToArray();
            var cluster = new List<ScanEvent>();
            var clusterEnd = double.MinValue;

            void Flush()
            {
                if (cluster.Count < 2)
                {
                    cluster.Clear();
                    return;
                }

                var start = cluster.Min(item => item.StartTime);
                var end = cluster.Max(item => item.EndTime);
                var key = Hash($"control|nearby|{group.Key:N}|{start:F1}|{end:F1}");
                var display = ClusterDisplay(group.Key, cluster);
                foreach (var item in cluster)
                {
                    replacements[item.Id] = item with
                    {
                        AggregateKey = key,
                        AggregateDisplay = display
                    };
                }
                cluster.Clear();
            }

            foreach (var item in ordered)
            {
                if (cluster.Count > 0 && item.StartTime > clusterEnd + NearbyEventGapSeconds)
                    Flush();
                cluster.Add(item);
                clusterEnd = Math.Max(clusterEnd, item.EndTime);
            }
            Flush();
        }

        return normalized
            .Select(item => replacements.GetValueOrDefault(item.Id, item))
            .OrderBy(item => item.StartTime)
            .ToArray();
    }

    private static string ClusterDisplay(Guid categoryID, IReadOnlyList<ScanEvent> cluster)
    {
        if (categoryID == ContentTaxonomy.Mappings["sexual_references"].CategoryID)
            return SexualClusterDisplay(cluster);
        if (categoryID == ContentTaxonomy.Mappings["violence_graphic"].CategoryID)
            return "A continuous scene of serious violence is described";
        if (categoryID == ContentTaxonomy.Mappings["blasphemy_statement"].CategoryID)
            return "A continuous passage includes blasphemous content";
        if (categoryID == ContentTaxonomy.Mappings["self_harm_reference"].CategoryID)
            return "A continuous passage includes self-harm content";
        return "Several related content events occur in one passage";
    }

    private static string SexualClusterDisplay(IReadOnlyList<ScanEvent> cluster)
    {
        var groups = cluster.Select(item => item.GroupID).ToHashSet();
        var nudity = ContentTaxonomy.Mappings["sexual_nudity"].GroupID;
        var implied = ContentTaxonomy.Mappings["sexual_implied_activity"].GroupID;
        var explicitActivity = ContentTaxonomy.Mappings["sexual_explicit_activity"].GroupID;
        var completeScene = ContentTaxonomy.Mappings["sexual_complete_scene"].GroupID;
        var references = ContentTaxonomy.Mappings["sexual_references"].GroupID;
        var suggestiveDialogue = ContentTaxonomy.Mappings["sexual_suggestive_dialogue"].GroupID;

        if (groups.Contains(nudity) && (groups.Contains(explicitActivity) || groups.Contains(completeScene)))
            return "A character removes clothing during an intimate encounter";
        if (groups.Contains(nudity))
            return "A character removes clothing or is described without clothing";
        if (groups.Contains(completeScene))
            return "Characters are described in a sustained intimate encounter";
        if (groups.Contains(explicitActivity))
            return "Characters are described in an intimate encounter";
        if (groups.Contains(implied))
            return "An intimate encounter is implied";
        if (groups.Contains(references) && groups.Contains(suggestiveDialogue))
            return "Suggestive dialogue and sexual references occur";
        if (groups.Contains(suggestiveDialogue))
            return "Suggestive dialogue occurs";
        return "A sexual reference is made";
    }

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
