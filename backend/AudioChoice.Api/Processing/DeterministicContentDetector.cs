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
    /// The first segment within a <see cref="CandidateWindows"/> range that actually matches
    /// the keyword cue, rather than the padded context around it.
    /// </summary>
    /// <remarks>
    /// A candidate window spans up to <c>contextSegments</c> on each side of the segment
    /// that matched -- several minutes on dense narration -- deliberately, since that
    /// padding is what gives Terra enough surrounding text to judge whether a full scene is
    /// happening. But a candidate that fails the scene bar and falls back to a lighter
    /// label (see OpenAIContentAnalysisProvider's AddKeywordSafetyNetFallback) must not
    /// inherit that same multi-minute span as its own boundary -- a short reference or one
    /// suggestive line is a short event, exactly the same rule the model's own prompt states
    /// for everything else it reports. This re-finds the one segment inside the window that
    /// is actually responsible for the window existing at all, so a fallback event can anchor
    /// to it instead of to the padding.
    /// </remarks>
    public static TranscriptSegment? FindMatchingCueSegment(
        IReadOnlyList<TranscriptSegment> windowSegments) =>
        windowSegments.FirstOrDefault(segment => SceneCueRegex().IsMatch(segment.Text));

    /// <summary>
    /// The matching cue segment together with the exact cue text matched inside it, so a
    /// caller can word-snap to the cue's own span rather than trusting the whole segment's
    /// boundary.
    /// </summary>
    /// <remarks>
    /// A transcript segment is frequently a full sentence or clause; the regex match itself
    /// can land anywhere inside it, not necessarily at the start. Reporting the matched
    /// segment's own StartTime/EndTime as a listener-facing boundary -- which is what this
    /// pipeline did before this was added -- means the reported range can start well before
    /// or run well past the words that actually justify it, however the transcriber happened
    /// to draw that segment's boundary and wherever the cue phrase happened to fall within
    /// it. Returning the matched substring lets the caller locate it against the segment's
    /// own word-level timing (see TranscriptWordLocator.FindPhrase) the same way every other
    /// confirmed event in this pipeline is boundary-snapped, instead of a lone exception
    /// trusting segment-level timing alone.
    /// </remarks>
    public static (TranscriptSegment Segment, string MatchedText)? FindMatchingCue(
        IReadOnlyList<TranscriptSegment> windowSegments)
    {
        foreach (var segment in windowSegments)
        {
            var match = SceneCueRegex().Match(segment.Text);
            if (match.Success) return (segment, match.Value);
        }
        return null;
    }

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
    // either. This is deliberately a high-recall net, not a classifier: a false-positive
    // candidate here costs one extra Terra call and is rejected there, while a false
    // negative costs a real miss reaching a listener, so the list below errs heavily toward
    // over-inclusion -- anatomy slang, act euphemisms, arousal/foreplay language, and common
    // literary fade-to-sex phrasing, on top of the clinical terms already present. Not
    // trusting Luna's prompt-level instruction that "euphemism still counts when the act is
    // unmistakable" alone: a prompt instruction is exactly as reliable as the model's own
    // recall on a given passage, and this regex exists specifically as the fallback for the
    // passages where that recall fails.
    [GeneratedRegex(
        @"\b(?:" +
            // Nudity / undressing.
            @"naked|nude|nudity|undress(?:ed|ing)?|strip(?:ped|ping)?|disrobe(?:d|ing)?|" +
            @"unbutton(?:ed|ing)?|unzip(?:ped|ping)?|unhook(?:ed|ing)?|unclasp(?:ed|ing)?|" +
            // General sexual terms.
            @"sex(?:ual|ually)?|orgasm(?:ed|ing)?|climax(?:ed|ing)?|" +
            @"intercourse|fornicat(?:e|ed|ing|ion)|copulat(?:e|ed|ing|ion)|" +
            // Acts.
            @"penetrat(?:e|ed|ing|ion)|thrust(?:ed|ing)?|grind(?:ing)?|ride|riding|" +
            @"moan(?:ed|ing)?|gasp(?:ed|ing)?|whimper(?:ed|ing)?|writh(?:e|ed|ing)?|" +
            @"kiss(?:ed|ing)?|lick(?:ed|ing)?|suck(?:ed|ing)?|nibbl(?:e|ed|ing)?|" +
            @"caress(?:ed|ing)?|fondl(?:e|ed|ing)?|grop(?:e|ed|ing)?|stroke[ds]?|strok(?:ed|ing)?|" +
            @"squeez(?:e|ed|ing)?|grip(?:ped|ping)?|grab(?:bed|bing)?|" +
            // Anatomy, clinical and slang.
            @"breasts?|nipples?|genitals?|groin|crotch|erect(?:ion)?|aroused?|arousal|" +
            @"wet(?:ness)?|hard(?:ness)?|throbbing|clit(?:oris|oral)?|vagina(?:l)?|penis|" +
            @"cock|dick|member|shaft|pussy|cunt|folds?|labia|testicles?|balls|scrotum|" +
            @"anus|anal|buttocks|ass(?:cheeks)?|rear\s+end|" +
            // Oral sex, every common name.
            @"oral\s+sex|blow[\s-]?job|blowing\s+him|fellatio|cunnilingus|" +
            @"going\s+down\s+on|head\s+(?:from|to)|" +
            // Anal sex.
            @"anal\s+sex|sodomy|sodomiz(?:e|ed|ing)|" +
            // Masturbation.
            @"masturbat(?:e|ed|ing|ion)|touch(?:ed|ing)?\s+herself|touch(?:ed|ing)?\s+himself|" +
            @"pleasur(?:e|ed|ing)\s+(?:herself|himself|themselves)|" +
            // Climax / ejaculation.
            @"cum(?:med|ming|shot)?|ejaculat(?:e|ed|ing|ion)|came\s+(?:inside|hard|undone)|" +
            @"release[ds]?\s+(?:himself|herself|inside)|" +
            // Assault.
            @"rape[ds]?|raping|assault(?:ed|ing)?|molest(?:ed|ing|ation)?|" +
            @"forced\s+himself|forced\s+herself|" +
            // Positioning / escalation cues.
            @"opening\s+(?:my|his|her|their)\s+legs|opened\s+(?:my|his|her|their)\s+legs|" +
            @"spreading\s+(?:my|his|her|their)\s+legs|spread\s+(?:my|his|her|their)\s+legs|" +
            @"between\s+(?:her|his|their)\s+(?:thighs?|legs)|straddl(?:e|ed|ing)|" +
            @"on\s+top\s+of\s+(?:him|her)|pinned\s+(?:him|her|them)|" +
            @"entered\s+her|entered\s+him|inside\s+her|inside\s+him|" +
            // Common literary fade-to-sex / aftermath phrasing.
            @"tangled\s+in\s+the\s+sheets|tangled\s+sheets|" +
            @"made\s+love|lovemaking|" +
            @"bed(?:room)?\s+that\s+night|rest\s+of\s+the\s+night\s+together|" +
            @"collapsed\s+(?:beside|next\s+to|on\s+top\s+of)\s+(?:him|her)|" +
            @"breathless(?:ly)?\s+(?:beside|against)|" +
            // Kissing, beyond a bare "kiss" -- the taxonomy's own sexual_kissing rung is
            // specifically about sustained/passionate kissing, which these phrases name
            // more precisely than the verb alone.
            @"passionate(?:ly)?\s+kiss(?:ed|ing)?|deepen(?:ed|ing)?\s+the\s+kiss|" +
            @"tongue(?:s)?\s+(?:met|tangled|danced)|" +
            // Suggestive dialogue / innuendo / references -- the rung the prompt's own
            // remarks call hardest to catch, since it is explicitly about what is implied
            // rather than stated. A keyword net cannot recognize true innuendo (that needs
            // judgment), but it can catch the stock phrases and body-language cues this
            // genre leans on most often to signal it.
            @"flirt(?:ed|ing|atious(?:ly)?)?|seduct(?:ive|ion)|seduc(?:e|ed|ing|tive)|" +
            @"undress(?:ed|ing)?\s+(?:him|her)\s+with\s+(?:his|her|their)\s+eyes|" +
            @"bit\s+(?:her|his)\s+lip|biting\s+(?:her|his)\s+lip|lip\s+bite|" +
            @"wanted\s+him|wanted\s+her|want(?:ed|ing)\s+each\s+other|" +
            @"desire\s+(?:burn|pool|ache)|desire\s+for\s+(?:him|her)|" +
            @"charged\s+(?:silence|moment|air)|sexual\s+tension|" +
            @"lingering\s+(?:touch|gaze|look)|" +
            @"warm\s+up\s+the\s+bed|see\s+what.{0,10}sheets\s+look\s+like|" +
            @"show\s+you\s+my\s+etchings|" +
            @"past\s+encounter|slept\s+together|spent\s+the\s+night\s+together|" +
            // Nudity-specific phrasing beyond "naked"/"nude" -- a body described as bare,
            // uncovered, or exposed is its own taxonomy rung (sexual_nudity) even with no
            // act and no reaction, which is exactly the omission this net exists to prevent.
            @"bare(?:ly)?\s+(?:skin|body|chest|breasts?)|" +
            @"expos(?:e|ed|ing)\s+(?:her|his|their)|" +
            @"uncover(?:ed|ing)\s+(?:her|his|their)|" +
            @"clothing\s+(?:fell|slipped|dropped|pooled)|" +
            @"shirt\s+(?:torn|ripped|came)\s+open|" +
            @"spilling\s+free|hardening\s+nipples?|" +
            // Consent / coercion language for sexual_violence, since that label's own
            // definition is judged specifically on what the passage establishes about
            // consent -- these are the phrases a passage most often uses to establish it.
            @"did(?:n't| not)\s+consent|without\s+(?:her|his|their)\s+consent|" +
            @"against\s+(?:her|his|their)\s+will|" +
            @"forced\s+(?:her|him|them)\s+(?:down|onto|against)|" +
            @"held\s+(?:her|him)\s+down|pinned\s+(?:her|him)\s+down|" +
            @"begged\s+him\s+to\s+stop|begged\s+her\s+to\s+stop|told\s+him\s+to\s+stop|" +
            @"tried\s+to\s+(?:push|fight|struggle)\s+(?:him|her)\s+(?:off|away)|" +
            @"could(?:n't| not)\s+move|could(?:n't| not)\s+fight\s+back|too\s+weak\s+to\s+resist|" +
            @"too\s+drunk\s+to\s+(?:consent|resist|stop\s+him|stop\s+her)" +
        @")\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SceneCueRegex();
}
