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

    public static string Format(BookFingerprint fingerprint)
    {
        var canonical = Canonicalize(fingerprint);
        return Format(canonical.WorkTitle, canonical.EditionType, canonical.PartNumber, canonical.TotalParts);
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
