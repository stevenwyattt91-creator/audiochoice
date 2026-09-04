using System.Text.RegularExpressions;
using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

public static class EditionTitleFormatter
{
    // Canonical Fourth Wing Part 1 source fingerprint. Some converted/imported
    // files lose their embedded part tags, so this immutable identity restores
    // the edition metadata before titles are persisted or published.
    private const string FourthWingPart1Sha256 =
        "3d37a3c485debd42249bc939deed657505d18c939bd43c00dae99e10800916e";
    private const long FourthWingPart1FileSize = 449954471;

    public static BookFingerprint Canonicalize(BookFingerprint fingerprint) =>
        IsFourthWingPart1(fingerprint)
            ? fingerprint with
            {
                EditionType = "Dramatized Adaptation",
                PartNumber = fingerprint.PartNumber ?? 1,
                TotalParts = fingerprint.TotalParts ?? 2,
            }
            : fingerprint;

    public static string Format(BookFingerprint fingerprint) => Format(fingerprint, productIdentifier: null);

    /// <summary>
    /// Renders a fingerprint's display title, preferring a known work's canonical name
    /// over whatever the file's own tags spelled.
    /// </summary>
    /// <remarks>
    /// A file's own title is only ever assembled from what one tagger wrote, or, for a
    /// GraphicAudio rip whose EPUB export ran every word together, from nothing usable at
    /// all. <see cref="KnownWorkCatalog"/> is checked first specifically so that every copy
    /// of one book -- an M4A, an M4B, a converted octet-stream, a run-together export --
    /// converges on the same name, which nothing that inspects only this one file's tags can
    /// guarantee. Falls back to the existing regex cleanup for a book the catalogue does not
    /// know, same as before this existed.
    /// </remarks>
    public static string Format(BookFingerprint fingerprint, string? productIdentifier)
    {
        var canonical = Canonicalize(fingerprint);
        var known = KnownWorkCatalog.FindByIdentifier(productIdentifier)
            ?? KnownWorkCatalog.FindByTitle(canonical.WorkTitle);
        return known is null
            ? Format(canonical.WorkTitle, canonical.EditionType, canonical.PartNumber, canonical.TotalParts)
            : FormatKnownWork(known, canonical);
    }

    /// <summary>
    /// Renders a known work's canonical title with the part this specific file states,
    /// if any, and its dramatization -- never the file's own spelling of the title itself.
    /// </summary>
    /// <remarks>
    /// <see cref="KnownWork.IsDramatized"/> is trusted outright once a work is matched,
    /// including by title alone. That is safe today because every known multi-part work in
    /// <see cref="KnownWorkCatalog"/> currently has exactly one edition AudioChoice imports
    /// -- the GraphicAudio dramatization -- and no plain narration of it has ever been
    /// scanned; a scanned file's own tags for these titles are frequently missing the word
    /// "dramatized" entirely; that is the exact defect this exists to correct. If a standard
    /// narration of one of these books is ever added, it needs a <see cref="KnownWork"/>
    /// entry of its own -- disambiguated by its own retail identifier, since a plain
    /// narration and a dramatization share a title -- rather than a single work entry
    /// deciding dramatization for both.
    /// </remarks>
    private static string FormatKnownWork(KnownWork known, BookFingerprint fingerprint)
    {
        // Matches "Part 1 of 2" and, since some rips drop the word "part" entirely and are
        // tagged just "(1 of 2)", the bare form too.
        var embeddedPart = System.Text.RegularExpressions.Regex.Match(
            fingerprint.WorkTitle ?? "", @"(?:part\s*)?(\d+)\s*(?:of|/)\s*(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // The work says how many parts it has. A file can still be missing its own part
        // number -- that is which piece *this copy* is, and the work cannot know that --
        // so only the part number, never the total, falls back to what the file states.
        var effectiveTotal = known.TotalParts is > 0
            ? known.TotalParts
            : fingerprint.TotalParts is > 0 ? fingerprint.TotalParts
            : embeddedPart.Success ? int.Parse(embeddedPart.Groups[2].Value)
            : null;
        var effectivePart = fingerprint.PartNumber is > 0
            ? fingerprint.PartNumber
            : embeddedPart.Success ? int.Parse(embeddedPart.Groups[1].Value)
            : null;

        var title = known.CanonicalTitle;
        if (effectivePart is > 0 && effectiveTotal is > 0) title += $" (Part {effectivePart} of {effectiveTotal})";
        if (known.IsDramatized) title += " (Dramatized Adaptation)";
        return title;
    }

    public static string Format(string? workTitle, string? editionType, int? partNumber, int? totalParts)
    {
        var title = string.IsNullOrWhiteSpace(workTitle) ? "Untitled Audiobook" : workTitle.Trim();
        // Preserve part metadata even when older imports only embedded it in the filename/title.
        var embeddedPart = Regex.Match(title, @"part\s*(\d+)\s*(?:of|/)\s*(\d+)", RegexOptions.IgnoreCase);
        var effectivePart = partNumber is > 0 ? partNumber : embeddedPart.Success ? int.Parse(embeddedPart.Groups[1].Value) : null;
        var effectiveTotal = totalParts is > 0 ? totalParts : embeddedPart.Success ? int.Parse(embeddedPart.Groups[2].Value) : null;
        title = Regex.Replace(title, @"\s*\(?\s*,?\s*part\s*\d+\s*(?:of|/)\s*\d+\s*\)?", "", RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"\s*\((?:dramatized adaptation|full cast|graphic audio)\)", "", RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"\s+[-–—:]?\s*(?:dramatized adaptation|full cast|graphic audio)\s*$", "", RegexOptions.IgnoreCase);
        title = Regex.Replace(title, "\\s+", " ").Trim();

        var isSpecial = (!string.IsNullOrWhiteSpace(editionType) &&
            (editionType.Contains("dramat", StringComparison.OrdinalIgnoreCase) ||
             editionType.Contains("full cast", StringComparison.OrdinalIgnoreCase) ||
             editionType.Contains("graphic audio", StringComparison.OrdinalIgnoreCase))) ||
            title.Contains("dramatized adaptation", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("full cast", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("graphic audio", StringComparison.OrdinalIgnoreCase);
        if (effectivePart is > 0 && effectiveTotal is > 0)
            title += $" (Part {effectivePart} of {effectiveTotal})";
        if (isSpecial)
            title += " (Dramatized Adaptation)";
        return title;
    }

    private static bool IsFourthWingPart1(BookFingerprint fingerprint) =>
        fingerprint.WorkTitle?.Contains("Fourth Wing", StringComparison.OrdinalIgnoreCase) == true &&
        (fingerprint.Sha256.Equals(FourthWingPart1Sha256, StringComparison.OrdinalIgnoreCase) ||
         fingerprint.FileSize == FourthWingPart1FileSize);
}
