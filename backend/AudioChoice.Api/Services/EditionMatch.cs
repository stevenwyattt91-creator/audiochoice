using System.Text;
using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

/// <summary>
/// Decides whether two file fingerprints describe the same recording.
/// </summary>
/// <remarks>
/// A fingerprint is a hash of file bytes, so converting an AAX to M4B or re-tagging
/// a file produces a completely different one for audio that is note-for-note
/// identical. Everything keyed by fingerprint -- transcripts above all -- then goes
/// missing. This is the rule that reconnects them.
///
/// It is deliberately conservative. A false negative costs a listener the
/// read-along feature for one book. A false positive would align their reader
/// against a *different* recording's timings, silently mistiming every filter and
/// every highlighted paragraph. So every check must pass, runtime evidence is
/// required rather than assumed, and dramatized adaptations can never match
/// straight readings.
/// </remarks>
public static class EditionMatch
{
    /// <summary>
    /// Container rewrapping and re-encoding shift a runtime by well under a second.
    /// This leaves room for that and for clients that round, while staying far below
    /// the gap between any two genuinely different recordings.
    /// </summary>
    public const double MaximumRuntimeDriftSeconds = 15.0;

    /// <summary>
    /// Chapter marks do not move when a container is rewrapped, so this only
    /// absorbs whole-second rounding by the reporting client.
    /// </summary>
    private const int MaximumChapterDriftSeconds = 2;

    public static bool SameRecording(
        BookFingerprint left,
        BookFingerprint right,
        EditionSignature? leftSignature = null,
        EditionSignature? rightSignature = null)
    {
        if (InMemoryScanCatalog.FingerprintKey(left) ==
            InMemoryScanCatalog.FingerprintKey(right))
        {
            return true;
        }

        // A recording and a text file can share a runtime, a retail identifier and even a
        // chapter structure -- an EPUB companion attached for read-along inherits exactly
        // those fields from the audiobook it was attached to, because that is deliberately
        // how read-along identifies which book a reading edition belongs to. None of that
        // makes the EPUB a second copy of the recording, so file kind is checked before
        // anything else and, like runtime, is never waived by a stated identifier.
        if (!SameFileKind(left, right)) return false;

        // Runtime is checked before anything else and is never waived.
        //
        // Signatures are reported by clients, and a matching product identifier is
        // enough for FindResult to hand over another edition's filter results. If an
        // identifier could answer on its own, a client that reported a borrowed ASIN
        // would redirect filters between unrelated recordings and could play content
        // a listener asked never to hear. Runtime is the one claim a tagger cannot
        // forge, so it stays a precondition for every match.
        if (!SameRuntime(left, right)) return false;

        // With runtime already agreed, a retail identifier names one published
        // edition and settles the rest in either direction: it is allowed to
        // override a disagreeing title, author or narrator, which is exactly the
        // case where a file's tags are wrong or missing.
        var verdict = ProductIdentifierVerdict(leftSignature, rightSignature);
        if (verdict is not null) return verdict.Value;

        // Evidence that rules a match out even when the title agrees.
        if (NarratorContradicts(leftSignature, rightSignature)) return false;
        if (ChaptersContradict(leftSignature, rightSignature)) return false;

        return SameTitle(left, right)
            && SameAuthor(left, right)
            && SamePart(left, right)
            && SamePresentation(left, right);
    }

    /// <summary>
    /// True or false when both sides state an identifier, null when at least one is
    /// silent and the question is still open.
    /// </summary>
    private static bool? ProductIdentifierVerdict(
        EditionSignature? left,
        EditionSignature? right)
    {
        var leftID = Normalize(left?.ProductIdentifier);
        var rightID = Normalize(right?.ProductIdentifier);
        if (leftID.Length == 0 || rightID.Length == 0) return null;
        return leftID == rightID;
    }

    /// <summary>
    /// Two different readings of the same book share a title, an author and often a
    /// near-identical runtime. The narrator is what separates them, and their
    /// transcripts are not interchangeable.
    /// </summary>
    private static bool NarratorContradicts(EditionSignature? left, EditionSignature? right)
    {
        var leftNarrator = Normalize(left?.Narrator);
        var rightNarrator = Normalize(right?.Narrator);
        if (leftNarrator.Length == 0 || rightNarrator.Length == 0) return false;

        // Casts and multi-narrator credits get listed in different orders and with
        // varying completeness, so containment either way counts as agreement.
        return !leftNarrator.Contains(rightNarrator, StringComparison.Ordinal)
            && !rightNarrator.Contains(leftNarrator, StringComparison.Ordinal);
    }

    /// <summary>
    /// A differing chapter structure means a differently produced edition. Silence on
    /// either side is not evidence: conversion tools routinely drop chapter marks, and
    /// treating that as a contradiction would lose a legitimate match.
    /// </summary>
    /// <summary>
    /// Whether two editions share a chapter structure detailed enough to identify one recording.
    /// </summary>
    /// <remarks>
    /// Chapter marks are already gathered and were only ever used to reject a match. As positive
    /// evidence they are the strongest thing available short of a retail identifier: a dozen marks
    /// agreeing to the second is a pattern a different reading of the same book does not share, and
    /// unlike a title or an ASIN it is not something a tagger types. Re-wrapping a container or
    /// converting a format does not move them, which is exactly the case that was failing -- the
    /// same recording, re-encoded, treated as a stranger.
    ///
    /// Eight marks is the floor. Below that the pattern is too small to be distinctive: two books
    /// of the same length with three evenly spaced parts would match each other.
    /// </remarks>
    public static bool ChapterStructureIdentifies(EditionSignature? left, EditionSignature? right)
    {
        var leftChapters = left?.ChapterOffsetSeconds;
        var rightChapters = right?.ChapterOffsetSeconds;
        if (leftChapters is not { } first || rightChapters is not { } second) return false;
        if (first.Count < MinimumIdentifyingChapters) return false;
        if (first.Count != second.Count) return false;
        return !first
            .Zip(second, (a, b) => Math.Abs(a - b))
            .Any(drift => drift > MaximumChapterDriftSeconds);
    }

    /// <summary>
    /// How many chapter marks a structure needs before it can identify a recording on its own.
    /// </summary>
    private const int MinimumIdentifyingChapters = 8;

    private static bool ChaptersContradict(EditionSignature? left, EditionSignature? right)
    {
        var leftChapters = left?.ChapterOffsetSeconds;
        var rightChapters = right?.ChapterOffsetSeconds;
        if (leftChapters is not { Count: > 1 } || rightChapters is not { Count: > 1 }) return false;
        if (leftChapters.Count != rightChapters.Count) return true;

        return leftChapters
            .Zip(rightChapters, (first, second) => Math.Abs(first - second))
            .Any(drift => drift > MaximumChapterDriftSeconds);
    }

    /// <summary>
    /// Runtime is the one signal a re-tag cannot forge, so it is required outright:
    /// an unknown duration on either side means there is nothing to corroborate a
    /// title with, and a title alone is not evidence.
    /// </summary>
    public static bool SameRuntime(BookFingerprint left, BookFingerprint right)
    {
        if (left.Duration is not > 0 || right.Duration is not > 0) return false;
        return Math.Abs(left.Duration.Value - right.Duration.Value) <= MaximumRuntimeDriftSeconds;
    }

    /// <summary>
    /// Whether two fingerprints describe files of the same kind -- both audio, or both
    /// text -- required alongside every other check because a recording and a reading
    /// edition are never the same file no matter what else agrees.
    /// </summary>
    /// <remarks>
    /// A read-along EPUB attached to an audiobook deliberately carries that audiobook's
    /// own duration, retail identifier and chapter offsets, because that is how the
    /// server tells which recording a reading edition belongs to. Without this check that
    /// same borrowed evidence would satisfy every other rule in <see cref="SameRecording"/>
    /// and <see cref="ChapterStructureIdentifies"/>, and the EPUB would be reported as a
    /// second copy of the recording rather than its companion. <c>"epub"</c> is the one
    /// non-audio <see cref="BookFingerprint.FileType"/> in use; everything else compares
    /// equal to itself and unequal to it.
    /// </remarks>
    public static bool SameFileKind(BookFingerprint left, BookFingerprint right) =>
        IsEbook(left) == IsEbook(right);

    private static bool IsEbook(BookFingerprint fingerprint) =>
        string.Equals(fingerprint.FileType?.Trim(), "epub", StringComparison.OrdinalIgnoreCase);

    private static bool SameTitle(BookFingerprint left, BookFingerprint right)
    {
        var leftTitle = Normalize(left.WorkTitle);
        var rightTitle = Normalize(right.WorkTitle);
        if (leftTitle.Length == 0 || rightTitle.Length == 0) return false;

        // Prefix rather than substring containment: one side often carries an extra
        // trailing qualifier such as a part or edition suffix. Allowing a match in
        // the middle of a title would let unrelated works collide.
        return leftTitle == rightTitle
            || leftTitle.StartsWith(rightTitle, StringComparison.Ordinal)
            || rightTitle.StartsWith(leftTitle, StringComparison.Ordinal);
    }

    /// <summary>
    /// Authors are frequently missing or written differently by each tagger, so a
    /// blank cannot be treated as a contradiction. Two *stated* and unrelated
    /// authors are.
    /// </summary>
    private static bool SameAuthor(BookFingerprint left, BookFingerprint right)
    {
        var leftAuthor = Normalize(left.Author);
        var rightAuthor = Normalize(right.Author);
        if (leftAuthor.Length == 0 || rightAuthor.Length == 0) return true;

        return leftAuthor == rightAuthor
            || leftAuthor.Contains(rightAuthor, StringComparison.Ordinal)
            || rightAuthor.Contains(leftAuthor, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two halves of a split audiobook can have near-identical runtimes, so a stated
    /// part number is decisive when both sides declare one.
    /// </summary>
    private static bool SamePart(BookFingerprint left, BookFingerprint right)
    {
        var leftPart = left.PartNumber ?? PartFromTitle(left.WorkTitle);
        var rightPart = right.PartNumber ?? PartFromTitle(right.WorkTitle);
        if (leftPart is null || rightPart is null) return true;
        return leftPart == rightPart;
    }

    /// <summary>
    /// A dramatized adaptation is a different performance with a different script.
    /// Its transcript must never be served for a straight reading, or vice versa.
    /// </summary>
    private static bool SamePresentation(BookFingerprint left, BookFingerprint right) =>
        IsDramatized(left) == IsDramatized(right);

    private static bool IsDramatized(BookFingerprint fingerprint)
    {
        var text = Normalize(fingerprint.EditionType) + Normalize(fingerprint.WorkTitle);
        return text.Contains("dramatiz", StringComparison.Ordinal)
            || text.Contains("dramatis", StringComparison.Ordinal)
            || text.Contains("graphicaudio", StringComparison.Ordinal);
    }

    private static int? PartFromTitle(string? title)
    {
        var normalized = Normalize(title);
        if (normalized.Length == 0) return null;

        // Matches the "part1of2" and "1of2" shapes left behind by normalization.
        var marker = System.Text.RegularExpressions.Regex.Match(
            normalized,
            @"(?:part)?(\d{1,2})of\d{1,2}",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromMilliseconds(100));
        if (marker.Success && int.TryParse(marker.Groups[1].Value, out var ofPart)) return ofPart;

        var part = System.Text.RegularExpressions.Regex.Match(
            normalized,
            @"part(\d{1,2})",
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromMilliseconds(100));
        return part.Success && int.TryParse(part.Groups[1].Value, out var plainPart) ? plainPart : null;
    }

    /// <summary>
    /// Reduces a value to lowercase letters and digits. Taggers disagree about
    /// punctuation, spacing, case and accents, and none of that carries identity.
    /// </summary>
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) ==
                System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }
}
