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
                var display = ClusterDisplay(group.Key);
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

    private static string ClusterDisplay(Guid categoryID)
    {
        if (categoryID == ContentTaxonomy.Mappings["sexual_references"].CategoryID)
            return "Related sexual content in a continuous passage";
        if (categoryID == ContentTaxonomy.Mappings["violence_graphic"].CategoryID)
            return "Related graphic violence in a continuous passage";
        if (categoryID == ContentTaxonomy.Mappings["blasphemy_statement"].CategoryID)
            return "Related blasphemous content in a continuous passage";
        if (categoryID == ContentTaxonomy.Mappings["self_harm_reference"].CategoryID)
            return "Related self-harm content in a continuous passage";
        return "Related content in a continuous passage";
    }

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
