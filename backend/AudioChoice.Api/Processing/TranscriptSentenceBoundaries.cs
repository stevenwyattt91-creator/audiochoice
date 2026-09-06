namespace AudioChoice.Api.Processing;

/// <summary>
/// Shared sentence-punctuation logic for the transcript's own text, used everywhere a
/// decision needs "is there a complete sentence here" rather than a flat number of seconds.
/// </summary>
/// <remarks>
/// Two independent callers need this: <see cref="SceneEventPostProcessor"/> decides whether
/// two scene candidates merge based on whether a complete sentence separates them, and
/// <see cref="OpenAIContentAnalysisProvider"/> sizes Terra's model-input context window by
/// expanding to the nearest sentence boundary rather than a flat ±20 seconds. Both need the
/// same abbreviation-aware terminator detection, so it lives here once rather than twice.
/// </remarks>
public static class TranscriptSentenceBoundaries
{
    /// <summary>
    /// Whether <paramref name="text"/> contains at least one ordinary sentence terminator,
    /// skipping the ones that are actually an abbreviation, an initial, or a decimal point.
    /// </summary>
    public static bool ContainsSentenceEnd(string text)
    {
        for (var index = 0; index < text.Length; index += 1)
        {
            if (text[index] is not ('.' or '!' or '?')) continue;

            var end = index + 1;
            while (end < text.Length && text[end] is '"' or '\'' or '\u2019' or '\u201d' or ')' or ']')
            {
                end += 1;
            }

            // A terminator with nothing after it, or nothing but whitespace, is the end of
            // the supplied text rather than a sentence break confirmed by what follows -- but
            // still counts as a complete sentence, since there is nowhere further to check.
            if (end >= text.Length || char.IsWhiteSpace(text[end]) ||
                !char.IsLetterOrDigit(text[end]))
            {
                if (!IsAbbreviationOrInitial(text, index)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether a complete, ordinary sentence sits in the transcript strictly between two
    /// points in time.
    /// </summary>
    /// <remarks>
    /// Shared by <see cref="SceneEventPostProcessor"/>'s merge decision (do two scene
    /// candidates merge?) and <see cref="OpenAIContentAnalysisProvider"/>'s Terra entry gate
    /// (are two weak references part of the same dense cluster?) -- both are really the same
    /// question: "is there a complete sentence of ordinary narrative between these two
    /// points, or are they part of one continuous passage?"
    /// </remarks>
    public static bool HasClearSentenceBetween(
        double rangeEnd,
        double nextRangeStart,
        IReadOnlyList<TranscriptSegment> segments)
    {
        var between = segments
            .Where(segment => segment.EndTime > rangeEnd && segment.StartTime < nextRangeStart)
            .OrderBy(segment => segment.StartTime)
            .ToArray();
        if (between.Length == 0) return false;

        var gapText = string.Join(' ', between.Select(segment => segment.Text));
        return ContainsSentenceEnd(gapText);
    }

    /// <summary>
    /// Whether <paramref name="text"/> ends, once trailing whitespace and closing
    /// quotes/brackets are stripped, on a genuine sentence terminator.
    /// </summary>
    /// <remarks>
    /// Used to decide whether one transcript segment is itself a complete sentence, which is
    /// what lets a context window stop expanding right after it rather than needing to look
    /// inside the next segment too.
    /// </remarks>
    public static bool EndsWithSentenceEnd(string text)
    {
        var end = text.TrimEnd().Length;
        while (end > 0 && text[end - 1] is '"' or '\'' or '\u2019' or '\u201d' or ')' or ']')
        {
            end -= 1;
        }
        if (end == 0) return false;
        var terminator = text[end - 1];
        if (terminator is not ('.' or '!' or '?')) return false;
        return !IsAbbreviationOrInitial(text, end - 1);
    }

    /// <summary>
    /// True when the full stop at <paramref name="terminator"/> belongs to an abbreviation
    /// or an initial ("Mr.", "J. R. R. Tolkien") rather than ending a sentence.
    /// </summary>
    private static bool IsAbbreviationOrInitial(string text, int terminator)
    {
        if (text[terminator] != '.') return false;

        var end = terminator;
        var start = end;
        while (start > 0 && char.IsLetter(text[start - 1])) start -= 1;
        var word = text[start..end];
        if (word.Length == 0) return false;

        // A single letter before a stop is an initial: "J. R. R. Tolkien".
        if (word.Length == 1 && char.IsUpper(word[0])) return true;

        return CommonAbbreviations.Contains(word);
    }

    private static readonly HashSet<string> CommonAbbreviations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Mr", "Mrs", "Ms", "Mx", "Dr", "Prof", "Rev", "Fr", "Sr", "Jr",
            "St", "Mt", "Lt", "Capt", "Col", "Gen", "Sgt", "Maj", "Adm",
            "vs", "etc", "eg", "ie", "cf", "al", "approx", "No", "Vol", "pp",
        };
}
