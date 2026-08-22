using System.Text.RegularExpressions;

namespace AudioChoice.Api.Processing;

/// <summary>
/// Produces timing-to-character ranges without returning private transcript text.
/// The EPUB text is supplied for one request and is deliberately not stored.
/// </summary>
public static partial class ReaderAlignment
{
    private const int SearchWindowWords = 20_000;
    private const int MinimumAnchorWords = 3;
    private const int AnchorOffsetLimit = 12;

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
            var words = Words(segment.Text);
            if (words.Count == 0) continue;

            var match = FindMatch(epubWords, words, cursor, positionsByWord);
            if (match is null) continue;

            // Use a reliable anchor anywhere near the start of the spoken section,
            // rather than requiring the narrator and EPUB to begin every segment
            // with the exact same word. That produces a dense map for all filters.
            var end = Math.Min(match.EpubStart + match.WordCount - 1, epubWords.Count - 1);
            result.Add(new ReaderTimingRange(
                segment.StartTime,
                segment.EndTime,
                epubWords[match.EpubStart].Start,
                epubWords[end].End));
            cursor = end + 1;
        }
        return result;
    }

    private static ReaderMatch? FindMatch(
        IReadOnlyList<ReaderWord> epub,
        IReadOnlyList<ReaderWord> transcript,
        int start,
        IReadOnlyDictionary<string, int[]> positionsByWord)
    {
        var searchEnd = Math.Min(epub.Count - MinimumAnchorWords, start + SearchWindowWords);
        var lastOffset = Math.Min(AnchorOffsetLimit, transcript.Count - MinimumAnchorWords);

        for (var offset = 0; offset <= lastOffset; offset++)
        {
            if (!positionsByWord.TryGetValue(transcript[offset].Value, out var candidates)) continue;
            foreach (var index in candidates)
            {
                if (index < start || index > searchEnd) continue;
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

    private static List<ReaderWord> Words(string text) =>
        WordPattern().Matches(text)
            .Select(match => new ReaderWord(
                match.Value.Replace('\u2019', '\'').ToLowerInvariant(), match.Index, match.Index + match.Length))
            .ToList();

    [GeneratedRegex("[\\p{L}\\p{N}]+(?:['\\u2019][\\p{L}\\p{N}]+)?")]
    private static partial Regex WordPattern();

    private sealed record ReaderWord(string Value, int Start, int End);
    private sealed record ReaderMatch(int EpubStart, int WordCount, int TranscriptOffset);
}

public sealed record ReaderTimingRange(
    double StartTime,
    double EndTime,
    int StartCharacter,
    int EndCharacter);
