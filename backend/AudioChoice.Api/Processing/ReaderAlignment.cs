using System.Text.RegularExpressions;

namespace AudioChoice.Api.Processing;

/// <summary>
/// Produces timing-to-character ranges without returning private transcript text.
/// The EPUB text is supplied for one request and is deliberately not stored.
/// </summary>
/// <remarks>
/// Word-level, not segment-level. A whisper segment can run ten or more words, and the
/// earlier version of this mapped an entire matched run to that segment's own
/// <see cref="TranscriptSegment.StartTime"/>/<see cref="TranscriptSegment.EndTime"/> even when
/// the matched words were only a few of the segment's own words -- so the reader's highlight,
/// and any filter mask computed from these ranges, moved at the pace of the whole segment
/// rather than the pace of the words actually matched. It also only ever looked for a starting
/// anchor in the first several words of a segment, so one noisy word near a segment's start
/// could cost every reliable word later in that same segment.
///
/// Both are fixed the same way: every matched word gets its own range, timed from the
/// transcript's own per-word timing when the transcriber supplied it (every
/// <see cref="FasterWhisperTranscriptionProvider"/> transcript does; an older transcript
/// produced before word timings existed may not), and the search for the next anchor
/// continues across the whole segment -- and past a local mismatch, within the same segment --
/// instead of stopping at a fixed word count or giving up on the segment entirely.
/// </remarks>
public static partial class ReaderAlignment
{
    private const int SearchWindowWords = 20_000;
    private const int MinimumAnchorWords = 3;

    public static IReadOnlyList<ReaderTimingRange> Create(
        IReadOnlyList<TranscriptSegment> transcript,
        string epubText)
    {
        var epubWords = Words(epubText);
        if (epubWords.Count == 0) return Array.Empty<ReaderTimingRange>();

        var positionsByWord = epubWords
            .Select((word, index) => (word.Value, index))
            .GroupBy(value => value.Value)
            .ToDictionary(group => group.Key, group => group.Select(value => value.index).ToArray());

        var result = new List<ReaderTimingRange>();
        var cursor = 0;
        foreach (var segment in transcript)
        {
            var spoken = SpokenWords(segment);
            if (spoken.Count == 0) continue;

            // Repeated within the same segment, not just once: a segment where the words
            // diverge partway through (a transcription slip, a narrator's aside the EPUB
            // does not carry) still has reliable words on the far side of that divergence,
            // and re-anchoring past it is what reaches them instead of abandoning the rest
            // of the segment.
            var transcriptOffset = 0;
            while (transcriptOffset <= spoken.Count - MinimumAnchorWords)
            {
                var match = FindMatch(epubWords, spoken, cursor, transcriptOffset, positionsByWord);
                if (match is null) break;

                // One range per matched word, not one for the whole run: each word carries
                // its own timing (real, when the transcriber reported it) and its own
                // character span, so a filter mask or the reader's highlight follows the
                // actual pace of speech through the run rather than a straight-line guess
                // across it.
                for (var index = 0; index < match.WordCount; index += 1)
                {
                    var word = spoken[match.TranscriptOffset + index];
                    var epubWord = epubWords[match.EpubStart + index];
                    result.Add(new ReaderTimingRange(
                        word.StartTime,
                        word.EndTime,
                        epubWord.Start,
                        epubWord.End));
                }

                cursor = match.EpubStart + match.WordCount;
                transcriptOffset = match.TranscriptOffset + match.WordCount;
            }
        }
        return result;
    }

    private static ReaderMatch? FindMatch(
        IReadOnlyList<ReaderWord> epub,
        IReadOnlyList<SpokenWord> transcript,
        int epubCursor,
        int fromTranscriptOffset,
        IReadOnlyDictionary<string, int[]> positionsByWord)
    {
        var searchEnd = Math.Min(epub.Count - MinimumAnchorWords, epubCursor + SearchWindowWords);
        var lastOffset = transcript.Count - MinimumAnchorWords;

        // Every remaining word in the segment is a candidate starting point, not just the
        // first several: the reliable anchor a segment contains is as likely to sit in its
        // second half as its first.
        for (var offset = fromTranscriptOffset; offset <= lastOffset; offset++)
        {
            if (!positionsByWord.TryGetValue(transcript[offset].Value, out var candidates)) continue;
            foreach (var index in candidates)
            {
                if (index < epubCursor || index > searchEnd) continue;
                var count = 0;
                while (offset + count < transcript.Count && index + count < epub.Count &&
                    string.Equals(epub[index + count].Value, transcript[offset + count].Value, StringComparison.Ordinal))
                {
                    count++;
                }
                if (count < MinimumAnchorWords) continue;

                // Candidate positions are sorted. The first reliable phrase after
                // the current cursor is the in-order reading position. Choosing a
                // later, longer repeated phrase made some events black out an
                // unrelated paragraph.
                return new ReaderMatch(index, count, offset);
            }
        }
        return null;
    }

    /// <summary>
    /// This segment's words with their own timing: the transcriber's per-word timestamps
    /// when it reported them, or an interpolation across the segment's own span when it did
    /// not.
    /// </summary>
    /// <remarks>
    /// The interpolated fallback is by character position within the segment's text rather
    /// than by word index, since a long word and a short one taking equal time is a worse
    /// approximation than none -- but it remains an approximation, which is exactly why the
    /// real per-word timing is preferred whenever the transcript carries it.
    /// </remarks>
    private static List<SpokenWord> SpokenWords(TranscriptSegment segment)
    {
        if (segment.Words is { Count: > 0 } words)
        {
            return words
                .Select(word => new SpokenWord(Normalize(word.Text), word.StartTime, word.EndTime))
                .Where(word => word.Value.Length > 0)
                .ToList();
        }

        var textLength = segment.Text.Length;
        if (textLength == 0) return [];
        var duration = segment.EndTime - segment.StartTime;

        return WordPattern().Matches(segment.Text)
            .Select(match => new SpokenWord(
                Normalize(match.Value),
                segment.StartTime + duration * ((double)match.Index / textLength),
                segment.StartTime + duration * ((double)(match.Index + match.Length) / textLength)))
            .Where(word => word.Value.Length > 0)
            .ToList();
    }

    private static List<ReaderWord> Words(string text) =>
        WordPattern().Matches(text)
            .Select(match => new ReaderWord(Normalize(match.Value), match.Index, match.Index + match.Length))
            .ToList();

    private static string Normalize(string value) => value.Replace('\u2019', '\'').ToLowerInvariant();

    [GeneratedRegex("[\\p{L}\\p{N}]+(?:['\\u2019][\\p{L}\\p{N}]+)?")]
    private static partial Regex WordPattern();

    private sealed record ReaderWord(string Value, int Start, int End);
    private sealed record SpokenWord(string Value, double StartTime, double EndTime);
    private sealed record ReaderMatch(int EpubStart, int WordCount, int TranscriptOffset);
}

public sealed record ReaderTimingRange(
    double StartTime,
    double EndTime,
    int StartCharacter,
    int EndCharacter);
