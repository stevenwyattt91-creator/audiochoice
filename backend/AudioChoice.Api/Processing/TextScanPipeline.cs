using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Processing;

/// <summary>
/// Produces filter events for a book that has no audiobook, by reading its text directly.
/// </summary>
/// <remarks>
/// Branches above <c>ScanPipeline.Process</c> rather than through it: there is no audio to
/// chunk, no transcription to pay for, and no transcript to store. What the two paths share
/// is the classifier and the taxonomy, which is what makes a narrated book's filter switches
/// behave like an audiobook's rather than like a second, subtly different system.
///
/// Nothing here writes the book's text anywhere. It arrives as an argument, is sliced into
/// passages that are views onto it, and goes out of scope when the request ends. The events
/// that survive carry offsets and neutral descriptions, never the words they were derived
/// from.
/// </remarks>
public sealed class TextScanPipeline(
    ITextContentAnalysisProvider analysisProvider,
    ILogger<TextScanPipeline> logger)
{
    /// <summary>
    /// Longest passage handed to the classifier before it is split at whitespace.
    /// </summary>
    /// <remarks>
    /// A ceiling for the pathological case -- a chapter with no sentence punctuation at all
    /// -- not a target. Ordinary prose splits at sentence boundaries well below this.
    /// </remarks>
    public const int MaximumPassageCharacters = 1_200;

    /// <summary>Rejected above this, mirroring the reader-alignment endpoint's bound.</summary>
    public const int MaximumBookTextCharacters = 8_000_000;

    public string ScannerVersion => analysisProvider.ScannerVersion;

    public async Task<NarrationTextScan> Scan(
        BookFingerprint fingerprint,
        string bookText,
        string? language,
        Action<double>? reportProgress,
        CancellationToken cancellationToken)
    {
        var passages = Passages(bookText, MaximumPassageCharacters);
        logger.LogInformation(
            "Text scan segmented {CharacterCount} characters into {PassageCount} passages.",
            bookText.Length,
            passages.Count);

        var events = passages.Count == 0
            ? []
            : await analysisProvider.AnalyzeCharacterOffsets(
                passages, reportProgress, cancellationToken);

        var usable = UsableEvents(events, bookText.Length);
        if (usable.Count != events.Count)
        {
            logger.LogWarning(
                "Discarded {DiscardedCount} of {EventCount} text scan events with offsets " +
                "outside the book's {CharacterCount} characters.",
                events.Count - usable.Count,
                events.Count,
                bookText.Length);
        }

        return new NarrationTextScan(
            usable,
            DateTimeOffset.UtcNow,
            analysisProvider.ScannerVersion,
            ScanContracts.TaxonomyVersion,
            bookText.Length,
            string.IsNullOrWhiteSpace(language) ? null : language.Trim());
    }

    /// <summary>
    /// Splits a book's text into passages whose start and end are character offsets into it.
    /// </summary>
    /// <remarks>
    /// Static and offset-exact so it can be tested against short strings without a model or
    /// a network.
    ///
    /// Splits at blank-line runs first, because a paragraph is the unit an author wrote in,
    /// then at sentence ends within a paragraph. Sentence-scale rather than paragraph-scale
    /// on purpose: a passage is the finest range a flag can be reported against, and a
    /// paragraph-scale passage would mean one profane word silences several hundred words
    /// around it.
    ///
    /// The sentence rule here is deliberately simpler than the client's <c>UnitSegmenter</c>,
    /// and no attempt is made to agree with it. The two are under opposite pressures. A
    /// wrong boundary on the client changes what a voice is asked to read aloud; here it only
    /// changes how finely a flag can be placed, and the client re-expands every range it
    /// receives to its own unit boundaries before anything is removed. Splitting too eagerly
    /// is therefore harmless in this direction, which is what allows the simpler rule.
    /// </remarks>
    public static IReadOnlyList<TranscriptSegment> Passages(
        string bookText,
        int maximumCharacters)
    {
        if (string.IsNullOrEmpty(bookText)) return [];
        var limit = Math.Max(1, maximumCharacters);
        var passages = new List<TranscriptSegment>();

        foreach (var (blockStart, blockEnd) in Blocks(bookText))
        {
            var cursor = blockStart;
            while (cursor < blockEnd)
            {
                var end = SentenceEnd(bookText, cursor, blockEnd, limit);
                Emit(passages, bookText, cursor, end);
                cursor = end;
            }
        }

        return passages;
    }

    /// <summary>
    /// Drops events a book's text cannot contain, and forces offsets onto whole characters.
    /// </summary>
    /// <remarks>
    /// The classifier returns doubles, because in the audio path they are seconds. An index
    /// into a string is not a fraction, and the client rejects a fractional offset outright
    /// on the grounds that anything fractional was produced as a time rather than as an
    /// index. Rounding here rather than there keeps that check meaningful instead of making
    /// it discard real events.
    ///
    /// Start rounds down and end rounds up, so a range only ever widens. Narrowing could
    /// leave the tail of a flagged word unfiltered, which is the one error a listener would
    /// certainly notice.
    /// </remarks>
    public static IReadOnlyList<ScanEvent> UsableEvents(
        IReadOnlyList<ScanEvent> events,
        int bookTextCharacters)
    {
        var usable = new List<ScanEvent>(events.Count);
        foreach (var item in events)
        {
            if (!double.IsFinite(item.StartTime) || !double.IsFinite(item.EndTime)) continue;

            var start = (int)Math.Floor(item.StartTime);
            var end = (int)Math.Ceiling(item.EndTime);
            if (start < 0) start = 0;
            if (end > bookTextCharacters) end = bookTextCharacters;

            // An empty or inverted range is not a passage. It usually means the classifier
            // echoed one offset twice, and keeping it would put a zero-width mask into the
            // client's merge step.
            if (end <= start) continue;

            usable.Add(item with { StartTime = start, EndTime = end });
        }

        return usable
            .OrderBy(item => item.StartTime)
            .ThenBy(item => item.EndTime)
            .ToArray();
    }

    /// <summary>Paragraph ranges, split at runs of newlines.</summary>
    private static IEnumerable<(int Start, int End)> Blocks(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && IsBlockBreak(text, index)) index += 1;
            if (index >= text.Length) break;

            var start = index;
            while (index < text.Length && !IsBlockBreak(text, index)) index += 1;
            yield return (start, index);
        }
    }

    private static bool IsBlockBreak(string text, int index) =>
        text[index] is '\n' or '\r';

    /// <summary>
    /// Where the sentence starting at <paramref name="cursor"/> ends, never past
    /// <paramref name="blockEnd"/> and never longer than <paramref name="limit"/>.
    /// </summary>
    private static int SentenceEnd(string text, int cursor, int blockEnd, int limit)
    {
        var hardLimit = Math.Min(blockEnd, cursor + limit);
        for (var index = cursor; index < hardLimit; index += 1)
        {
            if (text[index] is not ('.' or '!' or '?')) continue;

            // Trailing quotes and brackets belong to the sentence that closes them.
            var end = index + 1;
            while (end < blockEnd && text[end] is '"' or '\'' or '\u2019' or '\u201d' or ')' or ']')
            {
                end += 1;
            }

            // A terminator with no space after it is a decimal point, an ellipsis mid-word,
            // or an abbreviation the author ran together, not the end of a sentence.
            if (end >= blockEnd) return blockEnd;
            if (!char.IsWhiteSpace(text[end])) continue;
            if (SuppressesBreak(text, index)) continue;

            while (end < blockEnd && char.IsWhiteSpace(text[end])) end += 1;
            return end;
        }

        // No sentence end within the limit. Back off to the last whitespace so a word is
        // not cut in half, and only split mid-word if the text offers nowhere else.
        if (hardLimit >= blockEnd) return blockEnd;
        for (var index = hardLimit - 1; index > cursor; index -= 1)
        {
            if (!char.IsWhiteSpace(text[index])) continue;
            var end = index;
            while (end < blockEnd && char.IsWhiteSpace(text[end])) end += 1;
            return end;
        }
        return hardLimit;
    }

    /// <summary>
    /// True when a full stop is part of an abbreviation or an initial rather than a
    /// sentence end.
    /// </summary>
    /// <remarks>
    /// A short list on purpose. Over-splitting only makes ranges finer, so the cost of a
    /// missing entry is small; the entries that are here are the ones frequent enough in
    /// prose to fragment a great many passages.
    /// </remarks>
    private static bool SuppressesBreak(string text, int terminator)
    {
        if (text[terminator] != '.') return false;

        var end = terminator;
        var start = end;
        while (start > 0 && (char.IsLetter(text[start - 1]) || text[start - 1] == '.'))
        {
            start -= 1;
        }
        var word = text[start..end];
        if (word.Length == 0) return false;

        // A single letter before a stop is an initial: "J. R. R. Tolkien".
        if (word.Length == 1 && char.IsUpper(word[0])) return true;

        return Abbreviations.Contains(word);
    }

    private static readonly HashSet<string> Abbreviations =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Mr", "Mrs", "Ms", "Mx", "Dr", "Prof", "Rev", "Fr", "Sr", "Jr",
            "St", "Mt", "Lt", "Capt", "Col", "Gen", "Sgt", "Maj", "Adm",
            "vs", "etc", "eg", "ie", "cf", "al", "approx", "No", "Vol", "pp",
        };

    private static void Emit(
        List<TranscriptSegment> passages,
        string text,
        int start,
        int end)
    {
        // Offsets stay anchored to the original text, but the passage's own leading and
        // trailing whitespace is trimmed off both the text and the range. A range that
        // begins on a space would mask a space, which is invisible, and would make the
        // first flagged character of the passage one position off.
        var from = start;
        var to = end;
        while (from < to && char.IsWhiteSpace(text[from])) from += 1;
        while (to > from && char.IsWhiteSpace(text[to - 1])) to -= 1;
        if (to <= from) return;

        // Whitespace-only and punctuation-only passages carry nothing to classify. Chapter
        // rules and scene dividers would otherwise consume a batch slot each.
        var content = text[from..to];
        if (!content.Any(char.IsLetterOrDigit)) return;

        passages.Add(new TranscriptSegment(from, to, content));
    }
}
