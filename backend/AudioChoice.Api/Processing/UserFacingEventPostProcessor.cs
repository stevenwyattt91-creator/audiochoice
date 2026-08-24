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
    private const double NearbyViolenceGapSeconds = 12;

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

        var violenceCategory = ContentTaxonomy.Mappings["violence_graphic"].CategoryID;
        var replacements = new Dictionary<Guid, ScanEvent>();
        foreach (var group in normalized
                     .Where(item => item.CategoryID == violenceCategory)
                     .GroupBy(item => item.GroupID))
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
                var key = Hash($"control|violence|{group.Key:N}|{start:F1}|{end:F1}");
                var display = ViolenceDisplay(group.Key);
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
                if (cluster.Count > 0 && item.StartTime > clusterEnd + NearbyViolenceGapSeconds)
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

    private static string ViolenceDisplay(Guid groupID)
    {
        if (groupID == ContentTaxonomy.Mappings["violence_torture"].GroupID)
            return "Torture in a continuous passage";
        if (groupID == ContentTaxonomy.Mappings["violence_children"].GroupID)
            return "Violence involving children in a continuous passage";
        if (groupID == ContentTaxonomy.Mappings["violence_animals"].GroupID)
            return "Violence involving animals in a continuous passage";
        return "Graphic violence in a continuous passage";
    }

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
