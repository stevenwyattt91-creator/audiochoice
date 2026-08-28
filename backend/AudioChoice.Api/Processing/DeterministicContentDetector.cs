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

    // Lambda's local first pass deliberately keeps high-recall cues for the
    // non-sexual filters too. Mild/intense/death violence are excluded by policy;
    // graphic/torture/child/animal violence remain filterable.
    private static readonly IReadOnlyDictionary<string, Regex> CategoryCues =
        new Dictionary<string, Regex>(StringComparer.Ordinal)
        {
            ["violence_graphic"] = Cue(@"\b(?:gore|blood(?:y)?|dismember(?:ed|ment)?|behead(?:ed|ing)?|guts|eviscerat(?:e|ed|ion)|corpse|graphic violence)\b"),
            ["violence_torture"] = Cue(@"\b(?:torture|tortured|waterboarding|rack(?:ed|ing)?|electrocute(?:d|ion)?)\b"),
            ["violence_children"] = Cue(@"\b(?:child|children|kid|infant|baby)\b.{0,50}\b(?:killed|murdered|abused|tortured|stabbed|shot|beaten)\b"),
            ["violence_animals"] = Cue(@"\b(?:dog|cat|horse|animal|puppy|kitten)\b.{0,50}\b(?:killed|murdered|abused|tortured|stabbed|shot|beaten)\b"),
            ["substance_alcohol_use"] = Cue(@"\b(?:drink|drank|drinking|whiskey|whisky|vodka|beer|wine|bourbon|tequila|rum|alcohol)\b"),
            ["substance_intoxication"] = Cue(@"\b(?:drunk|intoxicated|blacked out|hungover|high)\b"),
            ["substance_drug_reference"] = Cue(@"\b(?:cocaine|heroin|meth|methamphetamine|fentanyl|opioid|marijuana|weed|pot|drug|drugs)\b"),
            ["substance_drug_use"] = Cue(@"\b(?:snort(?:ed|ing)?|inject(?:ed|ing)?|smoke|smoked|shoot up|take drugs|used cocaine|used heroin)\b"),
            ["substance_abuse_overdose"] = Cue(@"\b(?:overdose|overdosed|poison(?:ed|ing)?)\b"),
            ["blasphemy_religious_profanity"] = Cue(@"\b(?:goddamn|god damn|dammit|damn it|jesus christ)\b"),
            ["blasphemy_statement"] = Cue(@"\b(?:there is no god|god is dead|curse god|blasphem(?:e|y|ous))\b"),
            ["self_harm_reference"] = Cue(@"\b(?:self[- ]harm|cut myself|hurt myself|suicid(?:e|al)|kill myself)\b"),
            ["self_harm_suicidal_thoughts"] = Cue(@"\b(?:want to die|don't want to live|do not want to live|ending my life)\b"),
            ["self_harm_suicide_attempt"] = Cue(@"\b(?:attempted suicide|suicide attempt|tried to kill myself)\b"),
            ["self_harm_depiction"] = Cue(@"\b(?:cut her wrists|cut his wrists|cut my wrists|hanging himself|hanging herself|overdosed intentionally)\b")
        };

    /// <summary>
    /// Exact profane words, matched literally.
    /// </summary>
    /// <remarks>
    /// Reported at full confidence because there is no judgement involved: the word is
    /// either present or it is not. This is also what lets the apps group every occurrence
    /// of one word under a single switch, which needs the word itself rather than a
    /// model's paraphrase of it.
    /// </remarks>
    public static IReadOnlyList<DeterministicDetection> DetectProfanity(
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

    /// <summary>
    /// Cheap keyword cues, for deciding what deserves model review.
    /// </summary>
    /// <remarks>
    /// These are guesses, not findings, and they are wrong constantly: "high above the
    /// wall" matches intoxication, "a pot of coffee" matches a drug reference, and a single
    /// "bloody" matches graphic violence. They were previously emitted as user-facing
    /// events at 0.35 confidence, which nothing downstream ever checked, so a listener saw
    /// "Graphic violence described" on the strength of one word — and Luna's careful
    /// instruction to ignore ordinary conflict was bypassed entirely.
    ///
    /// They are only a defensible source of events where nothing else looks at the passage,
    /// which is the Lambda-first mode. Whenever Luna reads the whole transcript, its
    /// judgement supersedes these outright.
    /// </remarks>
    public static IReadOnlyList<DeterministicDetection> DetectCategoryCues(
        IReadOnlyList<TranscriptSegment> segments)
    {
        var detections = new List<DeterministicDetection>();
        foreach (var segment in segments)
        {
            foreach (var cue in CategoryCues)
            {
                if (!cue.Value.IsMatch(segment.Text)) continue;
                detections.Add(new(
                    cue.Key,
                    segment.StartTime,
                    segment.EndTime,
                    .35,
                    "Local Lambda content cue",
                    null));
            }
        }
        return detections;
    }

    private static Regex Cue(string pattern) => new(
        pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
        @"\b(?:damn|bastard|bitch|shit|fuck|fucking|motherfucker|asshole|naked|nude|sex|sexual|orgasm|rape|stabbed|shot|killed|murdered|blood|torture|suicide|overdose|opening\s+(?:my|his|her|their)\s+legs|opened\s+(?:my|his|her|their)\s+legs|spreading\s+(?:my|his|her|their)\s+legs|spread\s+(?:my|his|her|their)\s+legs)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ObviousContentRegex();

    [GeneratedRegex(
        @"\b(?:naked|nude|undress(?:ed|ing)?|sex(?:ual|ually)?|orgasm|climax|penetrat(?:e|ed|ing|ion)|thrust(?:ed|ing)?|moan(?:ed|ing)?|kiss(?:ed|ing)?|breast|nipples?|genitals?|erect(?:ion)?|masturbat(?:e|ed|ing|ion)|rape|assault(?:ed|ing)?|stab(?:bed|bing)?|shoot(?:ing|s)?|murder(?:ed|ing)?|blood(?:y)?|torture|suicide|overdose|opening\s+(?:my|his|her|their)\s+legs|opened\s+(?:my|his|her|their)\s+legs|spreading\s+(?:my|his|her|their)\s+legs|spread\s+(?:my|his|her|their)\s+legs|between\s+(?:her|his|their)\s+thighs?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SceneCueRegex();
}
