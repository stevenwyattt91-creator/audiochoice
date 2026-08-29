using System.Net.Http.Json;
using System.Text.Json;
using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

/// <summary>
/// Looks up a synopsis for a book whose file carries no description tag.
/// </summary>
public interface ISynopsisProvider
{
    /// <summary>
    /// Returns a usable synopsis for this edition, or null when none can be found.
    /// </summary>
    Task<string?> Find(BookFingerprint fingerprint, CancellationToken cancellationToken);
}

/// <summary>
/// Fetches synopses from Open Library.
/// </summary>
/// <remarks>
/// Chosen over Google Books because its terms permit storing what is fetched: Google's
/// require that results not be retained, which rules out putting a description in our own
/// database. Open Library needs no API key, and its bibliographic data is openly licensed.
///
/// Descriptions there are contributed by its community and are frequently drawn from
/// Wikipedia, so stored text carries an attribution line. That is also why
/// <see cref="ReadableSynopsis"/> exists: the field holds whatever someone entered, which is
/// often not a synopsis at all.
/// </remarks>
public sealed class OpenLibrarySynopsisProvider(
    HttpClient client,
    ILogger<OpenLibrarySynopsisProvider> logger) : ISynopsisProvider
{
    /// Credits the source, as reusing this text requires.
    public const string Attribution = "Description from Open Library.";

    public async Task<string?> Find(BookFingerprint fingerprint, CancellationToken cancellationToken)
    {
        var title = fingerprint.WorkTitle?.Trim();
        if (string.IsNullOrWhiteSpace(title)) return null;
        try
        {
            var match = await FindWork(title, fingerprint.Author, cancellationToken);
            if (match is null) return null;
            // The work record first, then that work's editions. A work's description is
            // community-written and is sometimes a passage from the book rather than a
            // summary; an edition's is usually the publisher's own copy. Red Rising is the
            // case in point: its work record holds dialogue from chapter one while its
            // editions carry the real synopsis, so stopping at the work found nothing usable.
            var candidates = new List<string> { match.Value.WorkKey };
            candidates.AddRange(match.Value.EditionKeys.Take(MaximumEditionLookups)
                .Select(key => $"/books/{key}"));
            foreach (var key in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var readable = ReadableSynopsis(await ReadDescription(key, cancellationToken));
                if (readable is not null) return $"{readable}\n\n{Attribution}";
            }
            return null;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            // A missing synopsis is not a failure worth propagating: the book still belongs in
            // the catalogue, and the next backfill can try again.
            logger.LogInformation(
                error, "Could not look up a synopsis for {Title}.", title);
            return null;
        }
    }

    /// <summary>
    /// How many of a work's editions to consult before giving up.
    /// </summary>
    /// <remarks>
    /// A popular book has dozens. Bounded so filling one book's synopsis cannot turn into
    /// thirty requests to someone else's service.
    /// </remarks>
    /// Eight because translations are interleaved with English printings and are skipped on
    /// sight: Red Rising's first English edition is the sixth listed.
    private const int MaximumEditionLookups = 8;

    /// <summary>Finds the work whose title and author best match this edition.</summary>
    private async Task<(string WorkKey, string[] EditionKeys)?> FindWork(
        string title, string? author, CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString(
            string.Join(' ', new[] { title, author }.Where(value => !string.IsNullOrWhiteSpace(value))));
        var response = await client.GetFromJsonAsync<JsonElement>(
            $"search.json?q={query}&fields=key,title,author_name,edition_key&limit=5", cancellationToken);
        if (!response.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var doc in docs.EnumerateArray())
        {
            if (!doc.TryGetProperty("key", out var key) || key.GetString() is not { } workKey) continue;
            // The title has to agree. Search is forgiving enough to return a different book by
            // the same author, and a wrong synopsis is worse than none.
            var candidateTitle = doc.TryGetProperty("title", out var found) ? found.GetString() : null;
            if (!TitlesAgree(title, candidateTitle)) continue;
            // Only check the author when both sides have one, since many files carry none.
            if (!string.IsNullOrWhiteSpace(author) &&
                doc.TryGetProperty("author_name", out var authors) &&
                authors.ValueKind == JsonValueKind.Array &&
                authors.EnumerateArray().Any() &&
                !authors.EnumerateArray().Any(value => AuthorsAgree(author, value.GetString())))
            {
                continue;
            }
            var editionKeys = doc.TryGetProperty("edition_key", out var editions) &&
                editions.ValueKind == JsonValueKind.Array
                ? editions.EnumerateArray()
                    .Select(value => value.GetString())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .ToArray()
                : [];
            return (workKey, editionKeys);
        }
        return null;
    }

    /// <param name="recordKey">A work or edition key, with or without a leading slash.</param>
    private async Task<string?> ReadDescription(string recordKey, CancellationToken cancellationToken)
    {
        var work = await client.GetFromJsonAsync<JsonElement>(
            $"{recordKey.TrimStart('/')}.json", cancellationToken);
        // A popular book's editions include translations, and those carry their description
        // in the translated language. Red Rising's first listed edition is Brazilian, so
        // taking the first edition with any description at all produced Portuguese prose.
        if (!IsEnglish(work)) return null;
        if (!work.TryGetProperty("description", out var description)) return null;
        // Older records store a bare string; newer ones a typed object.
        return description.ValueKind switch
        {
            JsonValueKind.String => description.GetString(),
            JsonValueKind.Object => description.TryGetProperty("value", out var value)
                ? value.GetString()
                : null,
            _ => null
        };
    }

    /// <summary>
    /// Whether a record is in English, so far as it says.
    /// </summary>
    /// <remarks>
    /// Records that declare no language are accepted: most are English, and work records
    /// generally omit the field entirely. Only an explicit other language is rejected.
    /// </remarks>
    public static bool IsEnglish(JsonElement record)
    {
        if (!record.TryGetProperty("languages", out var languages) ||
            languages.ValueKind != JsonValueKind.Array)
        {
            return true;
        }
        var declared = languages.EnumerateArray()
            .Select(value => value.TryGetProperty("key", out var key) ? key.GetString() : null)
            .Where(value => value is not null)
            .ToArray();
        return declared.Length == 0 ||
            declared.Any(value => value!.EndsWith("/eng", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The shortest text worth showing as a synopsis from a lookup.</summary>
    /// <remarks>
    /// Higher than the bar for a file's own description tag. A publisher's tag is at least
    /// meant to describe the book; this field holds whatever a stranger typed.
    /// </remarks>
    private const int MinimumLength = 80;
    private const int MaximumLength = 4000;

    /// <summary>
    /// Whether looked-up text actually reads as a description of the story.
    /// </summary>
    /// <remarks>
    /// Open Library's description field is free text, and a good share of it is not a
    /// synopsis: passages quoted from the book, one-line notes, series lists, or editorial
    /// remarks about the record itself. Red Rising is the case that prompted this — its entry
    /// opens with dialogue lifted from chapter one, which under a heading reading "About this
    /// audiobook" would look like the app had mangled the book.
    /// </remarks>
    public static string? ReadableSynopsis(string? value)
    {
        // Markdown emphasis and hard line breaks are common in these records and are noise
        // once the text is rendered as a paragraph.
        var trimmed = System.Text.RegularExpressions.Regex.Replace(value ?? "", @"\*\*|__", "");
        trimmed = System.Text.RegularExpressions.Regex.Replace(trimmed, @"\r\n?", "\n").Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length < MinimumLength) return null;

        // A quotation followed by a speech tag is dialogue, so this is a passage from the book
        // rather than a description of it. Opening on a quotation mark is deliberately not
        // enough on its own: publishers' own copy often leads with a pull-quote, and rejecting
        // that threw away the real synopsis for Iron Flame.
        if (SpeechTag.IsMatch(trimmed)) return null;

        // Almost entirely quotation is an excerpt however it is punctuated. The bar is high
        // because a blurb leading with a long pull-quote can legitimately be half quotation,
        // and rejecting those loses real synopses.
        var quoted = QuotedRun.Matches(trimmed).Sum(match => match.Length);
        if (quoted > trimmed.Length * 0.7) return null;

        // Notes about the catalogue record rather than the book.
        var lowered = trimmed.ToLowerInvariant();
        string[] recordNotes =
        [
            "this edition", "this record", "contains:", "omnibus of", "see also",
            "translated from", "source title", "originally published as"
        ];
        if (recordNotes.Any(note => lowered.StartsWith(note, StringComparison.Ordinal))) return null;

        // Wikipedia extracts carry a trailing licence footer that is not part of the summary.
        var separator = trimmed.IndexOf("([source][1])", StringComparison.OrdinalIgnoreCase);
        if (separator > MinimumLength) trimmed = trimmed[..separator].TrimEnd();

        return trimmed.Length > MaximumLength ? trimmed[..MaximumLength] : trimmed;
    }

    /// A closing quotation mark followed by an attribution: "...," she says.
    private static readonly System.Text.RegularExpressions.Regex SpeechTag = new(
        """["\u201D]\s*,?\s*(?:he|she|I|they|we|you)\s+(?:say|says|said|ask|asks|asked|repl|whisper|answer|murmur|shout|mutter)""",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// A run of quoted text long enough to be speech rather than a quoted title.
    private static readonly System.Text.RegularExpressions.Regex QuotedRun = new(
        """["\u201C][^"\u201D]{10,}["\u201D]""",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static bool TitlesAgree(string left, string? right) =>
        Simplify(left) is { Length: > 0 } first && Simplify(right) is { Length: > 0 } second &&
        (first == second || first.StartsWith(second, StringComparison.Ordinal) ||
         second.StartsWith(first, StringComparison.Ordinal));

    /// <remarks>
    /// Compares surnames rather than whole names, because files write authors as "Brown,
    /// Pierce" as often as "Pierce Brown".
    /// </remarks>
    private static bool AuthorsAgree(string left, string? right)
    {
        var first = Simplify(left).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var second = Simplify(right).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (first.Length == 0 || second.Length == 0) return false;
        return first.Intersect(second, StringComparer.Ordinal)
            .Any(part => part.Length > 2);
    }

    private static string Simplify(string? value) =>
        new string((value ?? "").Where(character => char.IsLetterOrDigit(character) || character == ' ')
            .ToArray())
            .ToLowerInvariant()
            .Replace("  ", " ")
            .Trim();
}
