namespace AudioChoice.Api.Processing;

/// <summary>
/// Finds where a phrase actually falls in a transcript's own word-level timing.
/// </summary>
/// <remarks>
/// Originally the profanity detector's alone: a spoken word carries its own timing from the
/// transcriber, and matching against that word list -- rather than against position, since a
/// regex's word count and the transcriber's do not always agree on punctuation, hyphenation
/// or contractions -- is what let a single "damn" be cut to its own three-quarters of a
/// second instead of the five-to-ten-second segment around it.
///
/// Generalized here because every other category has the same problem in a more serious
/// form. A model proposing "sexual_explicit_activity" does not name a word; it invents a
/// startTime and endTime directly, and the only check applied to that number is that it
/// falls somewhere inside the several-minutes-wide batch of segments it was shown. Nothing
/// ties the number to the words that supposedly justified it, which is how a real event's
/// timing lands on an entirely unrelated nearby sentence -- "crossing her arms and taking a
/// stance" cut under a sexual filter is not that sentence being misjudged; it is some other,
/// real event's timestamp missing its target. Requiring the model to also state which words
/// it means, and locating those words here before trusting any timestamp, closes that gap
/// for every category the same way it was already closed for profanity.
/// </remarks>
public static class TranscriptWordLocator
{
    /// <summary>
    /// Locates a phrase of one or more words inside a specific transcript segment's own word
    /// list, returning the exact span the transcriber measured for it.
    /// </summary>
    /// <remarks>
    /// Matched by comparing normalized word text in sequence, not by index and not by
    /// searching the segment's raw text -- the model's phrase, the transcriber's word
    /// boundaries and a regex over the segment text can each split contractions, hyphenation
    /// and punctuation differently, so only comparing word-to-word survives all three.
    /// Returns null on anything less than every word of the phrase matching in order,
    /// starting no earlier than <paramref name="searchFromIndex"/> so a claimed prefix of the
    /// word list is never matched again for a second, later occurrence of the same phrase.
    /// </remarks>
    public static WordSpan? FindPhrase(
        IReadOnlyList<TranscriptWord>? words,
        string phrase,
        int searchFromIndex = 0)
    {
        if (words is null || words.Count == 0) return null;
        var target = NormalizeWords(phrase);
        if (target.Length == 0) return null;

        for (var start = Math.Max(0, searchFromIndex); start + target.Length <= words.Count; start += 1)
        {
            var matched = true;
            for (var offset = 0; offset < target.Length; offset += 1)
            {
                if (!string.Equals(Normalize(words[start + offset].Text), target[offset],
                    StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }
            }
            if (!matched) continue;
            var end = start + target.Length - 1;
            return new WordSpan(start, end, words[start].StartTime, words[end].EndTime);
        }
        return null;
    }

    /// <summary>
    /// Locates one occurrence of a single word not already claimed by an earlier call.
    /// </summary>
    /// <remarks>
    /// The profanity detector's own shape: a segment can repeat the same profane word, and
    /// each occurrence must get its own timing rather than every one pointing at the first
    /// match. <paramref name="claimed"/> is a set rather than a search-from index because the
    /// occurrences of one word are not necessarily adjacent to each other, unlike the
    /// contiguous multi-word phrases <see cref="FindPhrase"/> exists for.
    /// </remarks>
    public static TranscriptWord? FindUnclaimedWord(
        IReadOnlyList<TranscriptWord>? words,
        string word,
        ISet<int> claimed)
    {
        if (words is null) return null;
        var target = Normalize(word);
        if (target.Length == 0) return null;
        for (var index = 0; index < words.Count; index += 1)
        {
            if (claimed.Contains(index)) continue;
            if (!string.Equals(Normalize(words[index].Text), target, StringComparison.OrdinalIgnoreCase)) continue;
            claimed.Add(index);
            return words[index];
        }
        return null;
    }

    /// <summary>
    /// Locates a phrase across a run of consecutive transcript segments, trying each
    /// segment's own word list as a possible start.
    /// </summary>
    /// <remarks>
    /// A model-proposed event's supporting text is not guaranteed to sit inside one segment:
    /// the transcriber's segment boundaries follow pauses in the audio, not sentence
    /// structure, so a short phrase can straddle two. Each candidate segment is searched
    /// independently rather than concatenating every word list in the range, because
    /// concatenation would let a phrase match across a gap the transcriber never actually
    /// heard as continuous -- the end of one segment's last word beside the start of an
    /// unrelated segment's first.
    /// </remarks>
    public static WordSpan? FindPhraseInSegments(
        IReadOnlyList<TranscriptSegment> segments,
        string phrase)
    {
        foreach (var segment in segments)
        {
            var found = FindPhrase(segment.Words, phrase);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>
    /// Locates a quoted phrase as a literal substring of a passage's own text, for the
    /// character-offset coordinate space a narrated ebook is scanned in rather than the
    /// word timings an audiobook transcript carries.
    /// </summary>
    /// <remarks>
    /// A book has no spoken words to time, but its passages carry the same guarantee an
    /// audio segment's word list does: <see cref="TranscriptSegment.StartTime"/> is this
    /// passage's own first character offset in the original flat text, so a substring match
    /// within <see cref="TranscriptSegment.Text"/> converts straight into an absolute
    /// character range the same way a matched word converts into an absolute second.
    /// </remarks>
    public static WordSpan? FindQuotedSubstring(
        IReadOnlyList<TranscriptSegment> segments,
        string quote)
    {
        var target = quote.Trim();
        if (target.Length == 0) return null;
        foreach (var segment in segments)
        {
            var index = segment.Text.IndexOf(target, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            var start = segment.StartTime + index;
            var end = start + target.Length;
            return new WordSpan(-1, -1, start, end);
        }
        return null;
    }

    /// <summary>
    /// Reduces a phrase to the sequence of normalized words <see cref="FindPhrase"/> compares
    /// against, using the same rule the caller's word list is normalized with.
    /// </summary>
    private static string[] NormalizeWords(string phrase) =>
        phrase
            .Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .Where(value => value.Length > 0)
            .ToArray();

    /// <summary>
    /// Strips the punctuation a transcriber attaches to a spoken word ("damn," or
    /// "-- stop") so a quoted phrase compares equal to the word list regardless of it.
    /// </summary>
    private static string Normalize(string value) =>
        value.Trim().Trim(',', '.', '!', '?', ';', ':', '"', '\u201C', '\u201D', '\'', '-', '\u2014', '\u2013')
            .ToLowerInvariant();
}

/// <summary>A located phrase's position and timing within the transcript it was found in.</summary>
public sealed record WordSpan(int StartWordIndex, int EndWordIndex, double StartTime, double EndTime);
