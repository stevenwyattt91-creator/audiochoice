namespace AudioChoice.Api.Contracts;

/// <summary>
/// Identity evidence about a recording that a file's byte hash cannot express.
/// </summary>
/// <remarks>
/// Reported by clients, which are the only party able to read it: the server sees
/// audio bytes during a scan but never the container tags. Held separately from
/// <see cref="BookFingerprint"/> on purpose -- that type is embedded in a great many
/// stored and serialized payloads, and widening it would mean shifting positional
/// column reads throughout the Postgres catalog for no gain.
///
/// Everything here is optional. A signature strengthens or rules out a match; its
/// absence only means falling back to comparing fingerprint metadata.
/// </remarks>
public sealed record EditionSignature(
    /// <summary>
    /// A retail product identifier, ASIN or ISBN, reduced to letters and digits.
    /// The only signal that identifies one published edition outright.
    /// </summary>
    string? ProductIdentifier = null,
    /// <summary>
    /// Distinguishes two different readings of the same book, which runtime and
    /// title alone cannot.
    /// </summary>
    string? Narrator = null,
    /// <summary>
    /// Chapter start offsets in whole seconds. A structural signature that survives
    /// re-encoding, because rewrapping a container does not move chapter marks.
    /// </summary>
    IReadOnlyList<int>? ChapterOffsetSeconds = null)
{
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(ProductIdentifier)
        && string.IsNullOrWhiteSpace(Narrator)
        && (ChapterOffsetSeconds is null || ChapterOffsetSeconds.Count == 0);
}
