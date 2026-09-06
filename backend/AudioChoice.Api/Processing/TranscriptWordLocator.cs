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
    /// Snaps a proposed range to the transcript's own nearest word boundaries.
    /// </summary>
    /// <remarks>
    /// The shared foundation every boundary-fixing step in the scanner builds on: a flat
    /// second-based pad or clamp is replaced everywhere with "the nearest place a word
    /// actually starts or ends", using the same word-level timing the profanity detector
    /// already trusts for a single word. Returns null when no supplied segment carries word
    /// timing at all, mirroring the existing fallback: a caller with no words to snap to
    /// keeps whatever range it already had rather than inventing one.
    /// </remarks>
    public static (double Start, double End)? SnapToNearestWord(
        IReadOnlyList<TranscriptSegment> segments,
        double proposedStart,
        double proposedEnd)
    {
        var words = FlattenWords(segments);
        if (words.Length == 0) return null;

        var start = NearestBoundary(words, proposedStart, useStart: true);
        var end = NearestBoundary(words, proposedEnd, useStart: false);
        if (end < start) end = start;
        return (start, end);
    }

    /// <summary>
    /// Expands a range outward by up to <paramref name="maxExpansionSeconds"/> on each side,
    /// then snaps that expanded edge to the nearest actual word boundary.
    /// </summary>
    /// <remarks>
    /// This is Sol's own rule -- up to one second of allowance on each end so a skip does not
    /// clip its own edges, with the allowance itself never landing mid-word. The expansion is
    /// a ceiling, not a fixed amount: if a word boundary sits closer than the cap, the edge
    /// moves only as far as that word; if none sits within the cap at all, the edge snaps to
    /// the nearest word to the original, unexpanded point instead of landing on nothing. This
    /// is deliberately not <see cref="SnapToNearestWord"/> called with a widened proposed
    /// range, because that would let the nearest word be arbitrarily far past the cap; here
    /// the cap always wins over distance.
    /// </remarks>
    public static (double Start, double End)? ExpandAndSnap(
        IReadOnlyList<TranscriptSegment> segments,
        double start,
        double end,
        double maxExpansionSeconds)
    {
        var words = FlattenWords(segments);
        if (words.Length == 0) return null;

        var expandedStart = SnapWithinCap(words, start, maxExpansionSeconds, useStart: true);
        var expandedEnd = SnapWithinCap(words, end, maxExpansionSeconds, useStart: false);
        if (expandedEnd < expandedStart) expandedEnd = expandedStart;
        return (expandedStart, expandedEnd);
    }

    /// <summary>
    /// The nearest word boundary beyond <paramref name="original"/>, in the direction the
    /// edge is expanding, that still falls within the expansion cap.
    /// </summary>
    /// <remarks>
    /// Deliberately the nearest such boundary and not the furthest one the cap could reach:
    /// the allowance exists so a skip does not clip a word already underway right at its
    /// edge, one word at a time, not to reach past it into words further away just because
    /// the total budget permits it. Falls back to the boundary nearest the original point
    /// (which may be the original point's own word) when nothing lies beyond it within the
    /// cap, so the edge still lands on a real word rather than an unsnapped number.
    /// </remarks>
    private static double SnapWithinCap(
        IReadOnlyList<TranscriptWord> words, double original, double maxExpansionSeconds, bool useStart)
    {
        var cap = Math.Max(0, maxExpansionSeconds);
        var beyondOriginal = useStart
            ? words.Select(word => word.StartTime).Where(value => value < original && value >= original - cap)
            : words.Select(word => word.EndTime).Where(value => value > original && value <= original + cap);

        return useStart
            ? (beyondOriginal.Any() ? beyondOriginal.Max() : NearestBoundary(words, original, useStart: true))
            : (beyondOriginal.Any() ? beyondOriginal.Min() : NearestBoundary(words, original, useStart: false));
    }

    /// <summary>The word start (or end) time closest to <paramref name="target"/>.</summary>
    private static double NearestBoundary(
        IReadOnlyList<TranscriptWord> words, double target, bool useStart)
    {
        var best = words[0];
        var bestDistance = Math.Abs((useStart ? best.StartTime : best.EndTime) - target);
        foreach (var word in words)
        {
            var distance = Math.Abs((useStart ? word.StartTime : word.EndTime) - target);
            if (distance < bestDistance)
            {
                best = word;
                bestDistance = distance;
            }
        }
        return useStart ? best.StartTime : best.EndTime;
    }

    /// <summary>Every word from every supplied segment that carries word timing, in order.</summary>
    private static TranscriptWord[] FlattenWords(IReadOnlyList<TranscriptSegment> segments) =>
        segments
            .Where(segment => segment.Words is { Count: > 0 })
            .SelectMany(segment => segment.Words!)
            .OrderBy(word => word.StartTime)
            .ToArray();

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
