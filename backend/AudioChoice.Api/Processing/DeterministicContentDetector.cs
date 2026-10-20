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
    // Profanity word-matching and the Lambda-first category-cue regexes (violence, substance
    // use, blasphemy, self-harm) were removed when this deployment was permanently narrowed
    // to sexual content only (see ContentTaxonomy.EnforcedLabels remarks) -- nothing calls
    // DetectProfanity/DetectCategoryCues any more, so the word lists and regexes that only
    // existed to support them were removed too rather than kept as dead data. The sexual-
    // content candidate-window scan below is unaffected: it is a real, active part of the
    // pipeline (OpenAIContentAnalysisProvider.AddUncoveredSexualCandidates) and stays.

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

    // Narrowed to sexual-content terms only; the violence/self-harm words this regex used to
    // also match (stabbed, shot, killed, murdered, blood, torture, suicide, overdose) were
    // removed along with those categories, so this assertion no longer fires on a book that
    // is violent but has no sexual content, which it otherwise would once nothing classifies
    // violence any more.
    [GeneratedRegex(
        @"\b(?:naked|nude|sex|sexual|orgasm|rape|opening\s+(?:my|his|her|their)\s+legs|opened\s+(?:my|his|her|their)\s+legs|spreading\s+(?:my|his|her|their)\s+legs|spread\s+(?:my|his|her|their)\s+legs)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ObviousContentRegex();

    // Narrowed to sexual-content terms only, for the same reason as ObviousContentRegex above
    // -- this decides which transcript windows get sent to the model as sexual-content
    // candidates, and a violence/self-harm cue here would just waste a model call on content
    // this deployment never classifies.
    //
    // Extended after a real miss: a completed scan (Stephen King's IT) reported no event for
    // a passage using "blowjob"/"blow job" and "clit", neither of which this list named, so
    // AddUncoveredSexualCandidates' keyword safety net (the one thing that is supposed to
    // catch a passage Luna's own model pass missed) had no cue to seed a Terra candidate from
    // either. Added the missing anatomy term (clit/clitoris/clitoral) and the missing oral-
    // sex act terms (blowjob/blow job, fellatio, cunnilingus, going down on) explicitly,
    // rather than only trusting Luna's prompt-level instruction that "euphemism still counts
    // when the act is unmistakable" -- a prompt instruction is exactly as reliable as the
    // model's own recall on a given passage, and this regex exists specifically as the
    // fallback for the passages where that recall fails.
    [GeneratedRegex(
        @"\b(?:naked|nude|undress(?:ed|ing)?|sex(?:ual|ually)?|orgasm|climax|penetrat(?:e|ed|ing|ion)|thrust(?:ed|ing)?|moan(?:ed|ing)?|kiss(?:ed|ing)?|breast|nipples?|genitals?|erect(?:ion)?|masturbat(?:e|ed|ing|ion)|rape|assault(?:ed|ing)?|opening\s+(?:my|his|her|their)\s+legs|opened\s+(?:my|his|her|their)\s+legs|spreading\s+(?:my|his|her|their)\s+legs|spread\s+(?:my|his|her|their)\s+legs|between\s+(?:her|his|their)\s+thighs?|clit(?:oris|oral)?|blow[\s-]?job|fellatio|cunnilingus|going\s+down\s+on|oral\s+sex|fuck(?:ed|ing)?|cock|pussy|dick|cum(?:med|ming)?|orgasm(?:ed|ing)?|vagina|penis|anus|anal\s+sex)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SceneCueRegex();
}
