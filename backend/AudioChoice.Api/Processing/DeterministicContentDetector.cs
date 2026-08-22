using System.Text.RegularExpressions;

namespace AudioChoice.Api.Processing;

public sealed record DeterministicDetection(
    string Label,
    double StartTime,
    double EndTime,
    double Confidence,
    string SafeDescription,
    string? ProfanityWord);

public static partial class DeterministicContentDetector
{
    private static readonly IReadOnlyDictionary<string, string> ProfanityLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["damn"] = "profanity_mild",
            ["hell"] = "profanity_mild",
            ["crap"] = "profanity_mild",
            ["ass"] = "profanity_mild",
            ["bastard"] = "profanity_strong",
            ["bitch"] = "profanity_strong",
            ["shit"] = "profanity_strong",
            ["fuck"] = "profanity_strong",
            ["fucking"] = "profanity_strong",
            ["motherfucker"] = "profanity_strong",
            ["asshole"] = "profanity_strong",
            ["dick"] = "profanity_sexual",
            ["cock"] = "profanity_sexual",
            ["pussy"] = "profanity_sexual",
            ["cunt"] = "profanity_sexual"
        };

    public static IReadOnlyList<DeterministicDetection> Detect(
        IReadOnlyList<TranscriptSegment> segments)
    {
        var detections = new List<DeterministicDetection>();
        foreach (var segment in segments)
        {
            foreach (Match match in WordRegex().Matches(segment.Text))
            {
                var word = match.Value.ToLowerInvariant();
                if (!ProfanityLabels.TryGetValue(word, out var label)) continue;
                detections.Add(new(
                    label,
                    segment.StartTime,
                    segment.EndTime,
                    1,
                    "Profanity detected",
                    word));
            }
        }
        return detections;
    }

    public static bool ContainsObviousContent(IReadOnlyList<TranscriptSegment> segments) =>
        segments.Any(segment => ObviousContentRegex().IsMatch(segment.Text));

    /// <summary>
    /// Produces high-recall transcript windows for the inexpensive first stage of the
    /// scanner funnel. This only decides what needs model review; it never creates a
    /// scene event by itself, so explicit-scene decisions remain with Luna/Terra/Sol.
    /// </summary>
    public static IReadOnlyList<(int StartIndex, int EndExclusive)> CandidateWindows(
        IReadOnlyList<TranscriptSegment> segments,
        int contextSegments = 12)
    {
        var ranges = new List<(int StartIndex, int EndExclusive)>();
        for (var index = 0; index < segments.Count; index += 1)
        {
            if (!SceneCueRegex().IsMatch(segments[index].Text)) continue;
            ranges.Add((Math.Max(0, index - contextSegments),
                Math.Min(segments.Count, index + contextSegments + 1)));
        }

        if (ranges.Count == 0) return ranges;
        var merged = new List<(int StartIndex, int EndExclusive)> { ranges[0] };
        foreach (var range in ranges.Skip(1))
        {
            var current = merged[^1];
            if (range.StartIndex <= current.EndExclusive)
            {
                merged[^1] = (current.StartIndex,
                    Math.Max(current.EndExclusive, range.EndExclusive));
            }
            else merged.Add(range);
        }
        return merged;
    }

    [GeneratedRegex(
        @"\b(?:damn|hell|crap|ass|bastard|bitch|shit|fuck|fucking|motherfucker|asshole|dick|cock|pussy|cunt)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    [GeneratedRegex(
        @"\b(?:damn|bastard|bitch|shit|fuck|fucking|motherfucker|asshole|naked|nude|sex|sexual|orgasm|rape|stabbed|shot|killed|murdered|blood|torture|suicide|overdose)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ObviousContentRegex();

    [GeneratedRegex(
        @"\b(?:naked|nude|undress(?:ed|ing)?|sex(?:ual|ually)?|orgasm|climax|penetrat(?:e|ed|ing|ion)|thrust(?:ed|ing)?|moan(?:ed|ing)?|kiss(?:ed|ing)?|breast|nipples?|genitals?|erect(?:ion)?|masturbat(?:e|ed|ing|ion)|rape|assault(?:ed|ing)?|stab(?:bed|bing)?|shoot(?:ing|s)?|murder(?:ed|ing)?|blood(?:y)?|torture|suicide|overdose)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SceneCueRegex();
}
