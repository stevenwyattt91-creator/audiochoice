namespace AudioChoice.Api.Processing;

/// <summary>
/// Confirms two transcripts describe the same recording by comparing their actual spoken
/// text, not by inferring it from metadata such as runtime.
/// </summary>
/// <remarks>
/// Built for exactly the case chapter structure and a retail identifier cannot reach: two
/// files whose reported runtimes disagree by tens of seconds not because they are different
/// recordings, but because one side's client-reported duration was simply wrong -- a real
/// case found on staging, where a file's own stored duration was 27 seconds short of its
/// measured length. Runtime alone could not settle that; the transcripts, compared directly,
/// could.
/// </remarks>
public static class TranscriptComparison
{
    /// <summary>
    /// How many evenly-spaced excerpts to compare across the shorter transcript's span.
    /// </summary>
    /// <remarks>
    /// One match could be a shared publisher intro that every edition of every audiobook
    /// from the same narrator carries; several, spread across the whole book, cannot be.
    /// </remarks>
    private const int CheckpointCount = 6;

    /// <summary>Consecutive words compared at each checkpoint.</summary>
    private const int WordsPerCheckpoint = 12;

    /// <summary>
    /// Returns null when the transcripts verbatim agree at every checkpoint, or a message
    /// describing the first checkpoint that did not.
    /// </summary>
    public static string? FindMismatch(PrivateTranscript left, PrivateTranscript right)
    {
        var leftWords = WordsOf(left);
        var rightWords = WordsOf(right);
        if (leftWords.Count < WordsPerCheckpoint || rightWords.Count < WordsPerCheckpoint)
        {
            return "One transcript is too short to compare with any confidence.";
        }

        // Checkpoints are placed by fraction of the way through the shorter transcript, so
        // a comparison never asks for a position past either transcript's own end even when
        // one runs measurably longer than the other.
        var shorterCount = Math.Min(leftWords.Count, rightWords.Count) - WordsPerCheckpoint;
        for (var checkpoint = 0; checkpoint < CheckpointCount; checkpoint += 1)
        {
            var fraction = (checkpoint + 1) / (double)(CheckpointCount + 1);
            var leftStart = (int)(fraction * (leftWords.Count - WordsPerCheckpoint));
            var rightStart = FindWindow(rightWords, leftWords, leftStart, shorterCount);
            if (rightStart is null)
            {
                var excerpt = string.Join(' ', leftWords.Skip(leftStart).Take(WordsPerCheckpoint));
                return $"Checkpoint {checkpoint + 1} of {CheckpointCount} did not match. " +
                    $"Looked for \"{excerpt}\" near the corresponding position in the other " +
                    "transcript and did not find it nearby.";
            }
        }
        return null;
    }

    /// <summary>
    /// Searches for the left transcript's word window in the right transcript, starting at
    /// the equivalent position and expanding outward.
    /// </summary>
    /// <remarks>
    /// Not an exact-index lookup, because the two transcriptions chunk the same audio into a
    /// different number of segments and so land on a different absolute word index for the
    /// same moment. A generous search radius absorbs that without becoming so wide that an
    /// unrelated but similarly-worded passage elsewhere in a long book could satisfy it.
    /// </remarks>
    private static int? FindWindow(
        IReadOnlyList<string> haystack,
        IReadOnlyList<string> needleSource,
        int needleStart,
        int searchRadius)
    {
        var needle = needleSource.Skip(needleStart).Take(WordsPerCheckpoint).ToArray();
        if (needle.Length < WordsPerCheckpoint) return null;

        var center = Math.Clamp(needleStart, 0, Math.Max(0, haystack.Count - WordsPerCheckpoint));
        var radius = Math.Max(searchRadius, 2_000);
        var low = Math.Max(0, center - radius);
        var high = Math.Min(haystack.Count - WordsPerCheckpoint, center + radius);

        for (var start = low; start <= high; start += 1)
        {
            var matched = true;
            for (var offset = 0; offset < needle.Length; offset += 1)
            {
                if (!string.Equals(haystack[start + offset], needle[offset], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }
            if (matched) return start;
        }
        return null;
    }

    /// <summary>
    /// The transcript's spoken text as a flat, normalized word sequence, in order.
    /// </summary>
    /// <remarks>
    /// Normalized the same way profanity matching already normalizes a spoken word: case
    /// folded and stripped of the punctuation two independent transcriptions of one
    /// recording routinely disagree about, so a real match is not missed over a comma.
    /// </remarks>
    private static List<string> WordsOf(PrivateTranscript transcript) =>
        transcript.Segments
            .SelectMany(segment => segment.Text.Split(
                [' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            .Select(word => word.Trim(
                ',', '.', '!', '?', ';', ':', '"', '\u201C', '\u201D', '\'', '-', '\u2014', '\u2013')
                .ToLowerInvariant())
            .Where(word => word.Length > 0)
            .ToList();
}
