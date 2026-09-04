namespace AudioChoice.Api.Services;

/// <summary>
/// One published work, named the way it should read everywhere in the app.
/// </summary>
/// <remarks>
/// A "work" is the book, not the file: <see cref="TotalParts"/> is set once for
/// "Fourth Wing" and applies to every part and every listener's copy of it, rather
/// than being re-derived per file from whatever a tagger happened to write. A file
/// still states its own <c>PartNumber</c> -- that is which piece *this copy* is --
/// but the work is what supplies the title, the author spelling and whether the
/// edition is a dramatized adaptation, so five different rips of the same
/// audiobook converge on one name instead of five.
/// </remarks>
public sealed record KnownWork(
    string CanonicalTitle,
    string Author,
    string? SeriesTitle,
    int? SeriesNumber,
    /// <summary>Null for a single-file work. Set for a work released or ripped in parts.</summary>
    int? TotalParts,
    bool IsDramatized,
    /// <summary>
    /// Retail identifiers (ASINs, ISBNs) known to belong to some edition of this work.
    /// </summary>
    /// <remarks>
    /// An identifier match is exact and settles the question outright, ahead of comparing
    /// titles at all. Different parts of a multi-part release usually carry different
    /// identifiers, so this holds every one seen, not just one per work.
    /// </remarks>
    IReadOnlyList<string> KnownIdentifiers)
{
    /// <summary>
    /// Normalized alternate titles a scanned or imported file might carry for this work,
    /// beyond the canonical title itself.
    /// </summary>
    /// <remarks>
    /// Populated for works whose files are seen with a materially different spelling --
    /// a subtitle, a series-qualified name, or a run-together filename with no spaces.
    /// Compared after the same normalization applied to an incoming title, so case,
    /// punctuation and spacing here do not matter.
    /// </remarks>
    public IReadOnlyList<string> AlternateTitles { get; init; } = [];
}

/// <summary>
/// Known audiobooks, used to render one canonical title for every scanned or imported
/// copy of a work regardless of how its tags or filename spelled it.
/// </summary>
/// <remarks>
/// Before this existed, <see cref="EditionTitleFormatter"/> could only strip and
/// re-append part/edition wording from whatever title a file already carried, and
/// exactly one work (Fourth Wing Part 1, matched by an immutable sha256) had a
/// hardcoded correction. Every other mis-tagged or run-together title -- an EPUB
/// export with no spaces, a GraphicAudio rip crediting "ep7", a plain "(1 of 2)"
/// with no author -- rendered exactly as it arrived, which is why the same book
/// could appear in Explore three times under three different names.
///
/// This is a curated, in-code list rather than a database table or an admin-editable
/// store, on purpose: the set of works AudioChoice has ever scanned is small, changes
/// only when a new book is added, and belongs in source control and code review like
/// the content taxonomy above it -- not behind a runtime API that could misname a
/// book with no review at all.
/// </remarks>
public static class KnownWorkCatalog
{
    public static readonly IReadOnlyList<KnownWork> Works =
    [
        new KnownWork(
            CanonicalTitle: "Fourth Wing",
            Author: "Rebecca Yarros",
            SeriesTitle: "The Empyrean",
            SeriesNumber: 1,
            TotalParts: 2,
            IsDramatized: true,
            KnownIdentifiers: ["9798890551030"]),

        new KnownWork(
            CanonicalTitle: "Iron Flame",
            Author: "Rebecca Yarros",
            SeriesTitle: "The Empyrean",
            SeriesNumber: 2,
            TotalParts: 2,
            IsDramatized: true,
            KnownIdentifiers: ["9798890552198"]),

        new KnownWork(
            CanonicalTitle: "A Court of Thorns and Roses",
            Author: "Sarah J. Maas",
            SeriesTitle: "A Court of Thorns and Roses",
            SeriesNumber: 1,
            TotalParts: 2,
            IsDramatized: true,
            KnownIdentifiers: ["9781685082758"])
        {
            // Seen run together with no spaces (an EPUB-derived export), and seen with the
            // series name folded into the title in place of the book number.
            AlternateTitles =
            [
                "acourtofthornsandrosesdramatizedadaptationacourtofthornsandrosesbook1",
                "a court of thorns and roses book 1",
            ],
        },

        new KnownWork(
            CanonicalTitle: "A Court of Mist and Fury",
            Author: "Sarah J. Maas",
            SeriesTitle: "A Court of Thorns and Roses",
            SeriesNumber: 2,
            TotalParts: 2,
            IsDramatized: true,
            KnownIdentifiers: ["9781685082772"])
        {
            AlternateTitles =
            [
                "a court of mist and fury a court of thorns and roses 2",
                "a court of mist and fury a court of thorns and roses book 2",
            ],
        },

        new KnownWork(
            CanonicalTitle: "Dungeon Crawler Carl",
            Author: "Matt Dinniman",
            SeriesTitle: "Dungeon Crawler Carl",
            SeriesNumber: 1,
            TotalParts: null,
            IsDramatized: false,
            KnownIdentifiers: []),

        new KnownWork(
            CanonicalTitle: "King Sorrow",
            Author: "Joe Hill",
            SeriesTitle: null,
            SeriesNumber: null,
            TotalParts: null,
            IsDramatized: false,
            KnownIdentifiers: ["B0DSCGNTXS"]),
    ];

    /// <summary>Every known identifier, mapped back to the work that carries it.</summary>
    private static readonly IReadOnlyDictionary<string, KnownWork> ByIdentifier =
        Works
            .SelectMany(work => work.KnownIdentifiers.Select(id => (id: NormalizeIdentifier(id), work)))
            .Where(pair => pair.id.Length > 0)
            .GroupBy(pair => pair.id, StringComparer.Ordinal)
            // First registration wins on a collision. None is expected -- a retail
            // identifier names one edition -- so a collision means a data entry error,
            // and silently overwriting one work's identifier with another's would be worse
            // than keeping whichever was written first.
            .ToDictionary(group => group.Key, group => group.First().work, StringComparer.Ordinal);

    /// <summary>Every known title (canonical and alternate), mapped back to its work.</summary>
    private static readonly IReadOnlyDictionary<string, KnownWork> ByTitle =
        Works
            .SelectMany(work => new[] { work.CanonicalTitle }.Concat(work.AlternateTitles)
                .Select(title => (title: NormalizeTitle(title), work)))
            .Where(pair => pair.title.Length > 0)
            .GroupBy(pair => pair.title, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().work, StringComparer.Ordinal);

    /// <summary>
    /// Finds the known work for a retail identifier, or null when the identifier is
    /// unknown or absent. Checked first by every caller: exact and unambiguous.
    /// </summary>
    public static KnownWork? FindByIdentifier(string? identifier)
    {
        var normalized = NormalizeIdentifier(identifier);
        return normalized.Length == 0
            ? null
            : ByIdentifier.GetValueOrDefault(normalized);
    }

    /// <summary>
    /// Finds the known work whose canonical or alternate title matches a scanned or
    /// imported title, once part markers and edition wording have been stripped from it.
    /// </summary>
    /// <remarks>
    /// A prefix match, not a substring one: "A Court of Thorns and Roses" must match a
    /// title that starts with it, but must not match "A Court of Mist and Fury" -- a
    /// completely different book in the same series -- just because both share a
    /// possible common prefix after normalization. Matched against whichever known
    /// title is longer first, so a title containing both a book's own name and its
    /// series-mate's does not resolve to the wrong one.
    /// </remarks>
    public static KnownWork? FindByTitle(string? title)
    {
        var normalized = NormalizeTitle(title);
        if (normalized.Length == 0) return null;
        if (ByTitle.TryGetValue(normalized, out var exact)) return exact;

        return ByTitle
            .Where(entry => normalized.StartsWith(entry.Key, StringComparison.Ordinal)
                || entry.Key.StartsWith(normalized, StringComparison.Ordinal))
            .OrderByDescending(entry => entry.Key.Length)
            .Select(entry => entry.Value)
            .FirstOrDefault();
    }

    /// <summary>
    /// Reduces a title to lowercase letters and digits, with every known piece of part
    /// and edition wording removed, so two spellings of one book's name compare equal.
    /// </summary>
    internal static string NormalizeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var text = value.ToLowerInvariant();
        // A track-list or import-order number left in front of the title, such as the "1 "
        // in "1 A Court of Thorns and Roses...". Stripped only when more text follows, so a
        // title that is itself a bare number -- "1984" has none after it -- is left alone.
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"^\d{1,3}\s+(?=[a-z])", "",
            System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"part\s*\d+\s*(?:of|/)\s*\d+", " ",
            System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"\b\d+\s*(?:of|/)\s*\d+\b", " ",
            System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"dramatized adaptation|dramatised adaptation|\bdramatized\b|\bdramatised\b|" +
            @"full cast production|full cast|graphicaudio|graphic audio|unabridged|abridged|\bep\s*\d+\b",
            " ", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100));
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character)) builder.Append(character);
            else if (builder.Length > 0 && builder[^1] != ' ') builder.Append(' ');
        }
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Reduces an identifier to uppercase letters and digits for comparison.</summary>
    private static string NormalizeIdentifier(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
}
