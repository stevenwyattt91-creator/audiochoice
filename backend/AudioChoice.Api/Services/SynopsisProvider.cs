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
    /// <param name="productIdentifier">
    /// The retail identifier the file reported, when it had one. An ISBN pins the edition
    /// exactly; an Audible ASIN does not help, because those are not indexed.
    /// </param>
    Task<string?> Find(
        BookFingerprint fingerprint,
        string? productIdentifier,
        CancellationToken cancellationToken);
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

    public async Task<string?> Find(
        BookFingerprint fingerprint,
        string? productIdentifier,
        CancellationToken cancellationToken)
    {
        var title = fingerprint.WorkTitle?.Trim();
        var isbn = AsISBN(productIdentifier);
        if (string.IsNullOrWhiteSpace(title) && isbn is null) return null;
        try
        {
            // An ISBN names one edition outright, so it settles which book this is without
            // matching titles. Only some files carry one, and Audible's own identifiers are
            // not indexed here, so a title search remains the general case.
            var candidates = new List<string>();
            var byISBN = await FindWorkByISBN(isbn, cancellationToken);
            if (byISBN is not null) candidates.Add(byISBN);
            if (!string.IsNullOrWhiteSpace(title))
            {
                candidates.AddRange(await FindWorks(title, fingerprint.Author, cancellationToken));
            }

            // Several works can describe one book. Open Library holds sparse duplicates
            // alongside the canonical record, and its search names a work by whichever title
            // ranks first, which for A Court of Mist and Fury is the Spanish one. Settling for
            // the first candidate whose title matched therefore chose a duplicate holding no
            // description at all, so each is tried in turn instead.
            foreach (var workKey in candidates.Distinct(StringComparer.Ordinal).Take(MaximumWorksTried))
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The work record first, then that work's editions. A work's description is
                // community-written and is sometimes a passage from the book rather than a
                // summary; an edition's is usually the publisher's own copy. Red Rising is the
                // case in point: its work record holds dialogue from chapter one while its
                // editions carry the real synopsis.
                var fromWork = ReadableSynopsis(await ReadDescription(workKey, cancellationToken));
                if (fromWork is not null) return $"{fromWork}\n\n{Attribution}";

                var fromEdition = await FindEditionDescription(workKey, cancellationToken);
                if (fromEdition is not null) return $"{fromEdition}\n\n{Attribution}";
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
    /// How many of a work's editions to read.
    /// </summary>
    /// <remarks>
    /// They arrive in one response, so this bounds how much is parsed rather than how many
    /// requests are made. Generous because the English printing can sit a long way down a
    /// popular book's list: A Court of Mist and Fury has twenty-nine editions, most of them
    /// translations, and the usable English description is well past the first handful.
    /// </remarks>
    private const int EditionPageSize = 50;

    /// <summary>
    /// The first usable English description among a work's editions.
    /// </summary>
    /// <remarks>
    /// One request for the whole list rather than one per edition. Fetching them individually
    /// meant choosing a cut-off, and any cut-off small enough to be polite was too small:
    /// Red Rising's English edition is sixth and A Court of Mist and Fury's is further still.
    /// </remarks>
    private async Task<string?> FindEditionDescription(
        string workKey, CancellationToken cancellationToken)
    {
        var response = await client.GetFromJsonAsync<JsonElement>(
            $"{workKey.TrimStart('/')}/editions.json?limit={EditionPageSize}", cancellationToken);
        if (!response.TryGetProperty("entries", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var edition in entries.EnumerateArray())
        {
            if (!IsEnglish(edition)) continue;
            var readable = ReadableSynopsis(DescriptionOf(edition));
            if (readable is not null) return readable;
        }
        return null;
    }

    /// <summary>
    /// The identifier as an ISBN, or null when it is not one.
    /// </summary>
    /// <remarks>
    /// Files report either an ISBN or an Audible ASIN in the same field. Only the ISBN is
    /// usable here: Open Library indexes Amazon print identifiers, and a check against real
    /// records found Audible's own ASINs are absent from it entirely.
    /// </remarks>
    public static string? AsISBN(string? value)
    {
        var digits = new string((value ?? "").Where(char.IsLetterOrDigit).ToArray());
        if (ExploreCatalog.IsAudibleProductIdentifier(digits)) return null;
        // ISBN-13 is all digits; ISBN-10 may end in X as its check character.
        if (digits.Length == 13 && digits.All(char.IsDigit)) return digits;
        if (digits.Length == 10 &&
            digits[..9].All(char.IsDigit) &&
            (char.IsDigit(digits[9]) || digits[9] is 'X' or 'x'))
        {
            return digits.ToUpperInvariant();
        }
        return null;
    }

    /// <summary>
    /// The work an ISBN belongs to, taken straight from the edition record.
    /// </summary>
    /// <remarks>
    /// Exact where a title search is a guess, which matters because a wrong match means a
    /// synopsis for a different book. The edition itself often carries no description, so its
    /// value is in naming the work rather than in what it holds.
    /// </remarks>
    private async Task<string?> FindWorkByISBN(string? isbn, CancellationToken cancellationToken)
    {
        if (isbn is null) return null;
        try
        {
            var edition = await client.GetFromJsonAsync<JsonElement>(
                $"isbn/{isbn}.json", cancellationToken);
            if (!edition.TryGetProperty("works", out var works) ||
                works.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            foreach (var work in works.EnumerateArray())
            {
                if (work.TryGetProperty("key", out var key) && key.GetString() is { } workKey)
                {
                    return workKey;
                }
            }
            return null;
        }
        catch (HttpRequestException)
        {
            // An ISBN Open Library does not hold is an ordinary miss, not a failure.
            return null;
        }
    }

    /// How many works to consult before giving up, bounding requests per book.
    private const int MaximumWorksTried = 3;

    /// <summary>
    /// Works that could be this edition, best first.
    /// </summary>
    /// <remarks>
    /// Ordered rather than reduced to one. A title match is the strongest signal, so those
    /// come first; after them come works whose author agrees but whose listed title does not,
    /// because that title may simply be a translation. Within each group the work with more
    /// editions wins, which is a good proxy for the canonical record over a stub.
    /// </remarks>
    private async Task<List<string>> FindWorks(
        string title, string? author, CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString(
            string.Join(' ', new[] { title, author }.Where(value => !string.IsNullOrWhiteSpace(value))));
        var response = await client.GetFromJsonAsync<JsonElement>(
            $"search.json?q={query}&fields=key,title,author_name,edition_count&limit=5",
            cancellationToken);
        if (!response.TryGetProperty("docs", out var docs) || docs.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var titleMatches = new List<(string Key, int Editions)>();
        var authorMatches = new List<(string Key, int Editions)>();
        foreach (var doc in docs.EnumerateArray())
        {
            if (!doc.TryGetProperty("key", out var key) || key.GetString() is not { } workKey) continue;
            // The author has to agree whenever both sides name one, since search is forgiving
            // enough to return an unrelated book and a wrong synopsis is worse than none.
            var authorKnown = !string.IsNullOrWhiteSpace(author) &&
                doc.TryGetProperty("author_name", out var authors) &&
                authors.ValueKind == JsonValueKind.Array &&
                authors.EnumerateArray().Any();
            if (authorKnown &&
                !doc.GetProperty("author_name").EnumerateArray()
                    .Any(value => AuthorsAgree(author!, value.GetString())))
            {
                continue;
            }
            var editions = doc.TryGetProperty("edition_count", out var count) &&
                count.ValueKind == JsonValueKind.Number
                ? count.GetInt32()
                : 0;
            var candidateTitle = doc.TryGetProperty("title", out var found) ? found.GetString() : null;
            if (TitlesAgree(title, candidateTitle)) titleMatches.Add((workKey, editions));
            else if (authorKnown) authorMatches.Add((workKey, editions));
        }

        return titleMatches.OrderByDescending(value => value.Editions)
            .Concat(authorMatches.OrderByDescending(value => value.Editions))
            .Select(value => value.Key)
            .ToList();
    }

    /// <param name="recordKey">A work or edition key, with or without a leading slash.</param>
    private async Task<string?> ReadDescription(string recordKey, CancellationToken cancellationToken)
    {
        var record = await client.GetFromJsonAsync<JsonElement>(
            $"{recordKey.TrimStart('/')}.json", cancellationToken);
        // A popular book's editions include translations, and those carry their description
        // in the translated language. Red Rising's first listed edition is Brazilian, so
        // taking the first edition with any description at all produced Portuguese prose.
        return IsEnglish(record) ? DescriptionOf(record) : null;
    }

    /// <summary>Reads a record's description, in either of the shapes Open Library uses.</summary>
    private static string? DescriptionOf(JsonElement record)
    {
        if (!record.TryGetProperty("description", out var description)) return null;
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

        // Translated text that the record does not admit to. Editions frequently declare no
        // language at all, so the declared field cannot be the only check: Dungeon Crawler
        // Carl's French edition and Project Hail Mary's Spanish one both declare nothing and
        // would otherwise have been stored against an English audiobook.
        if (!LooksEnglish(trimmed)) return null;

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

    /// <summary>
    /// Whether text reads as English, judged on how much of it is English function words.
    /// </summary>
    /// <remarks>
    /// Deliberately crude, because it only has to separate English prose from another
    /// language entirely. Measured against real records the margin is wide: English
    /// descriptions run a fifth to a third function words, while French and Spanish ones
    /// score nothing at all, so the threshold sits far below anything English.
    /// </remarks>
    public static bool LooksEnglish(string? value)
    {
        var words = WordPattern.Matches((value ?? "").ToLowerInvariant())
            .Select(match => match.Value)
            .ToArray();
        if (words.Length == 0) return false;
        var hits = words.Count(EnglishFunctionWords.Contains);
        return hits >= 3 && hits >= words.Length * 0.05;
    }

    private static readonly System.Text.RegularExpressions.Regex WordPattern = new(
        "[a-z']+", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// Words common in English prose and absent from the languages being screened out.
    private static readonly HashSet<string> EnglishFunctionWords = new(StringComparer.Ordinal)
    {
        "the", "and", "of", "to", "with", "his", "her", "that", "from", "was", "who",
        "which", "they", "she", "he", "is", "are", "has", "have", "been", "will", "not",
        "but", "their", "when", "all"
    };

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
