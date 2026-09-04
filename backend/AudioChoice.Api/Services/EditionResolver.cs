using AudioChoice.Api.Contracts;
using AudioChoice.Api.Processing;

namespace AudioChoice.Api.Services;

/// <summary>
/// Finds a book's stored artifacts even when the caller's fingerprint is not the one
/// they were stored under.
/// </summary>
public interface IEditionResolver
{
    /// <summary>
    /// Locates read-along timing data, matching on metadata when necessary.
    /// </summary>
    Task<PrivateTranscript?> LoadTranscript(
        BookFingerprint fingerprint,
        CancellationToken cancellationToken);

    /// <summary>
    /// Locates an existing filter result, but only on evidence strong enough to be
    /// certain. See the remarks on <see cref="EditionResolver"/>.
    /// </summary>
    ScanResult? FindResult(BookFingerprint fingerprint);
}

/// <summary>
/// Resolves a fingerprint to stored artifacts in escalating steps.
/// </summary>
/// <remarks>
/// The problem this solves: <see cref="ScanPipeline"/> saves a transcript under the
/// fingerprint of the bytes that were *uploaded*, while a library row can carry a
/// different fingerprint for the same recording -- the client deliberately adopts a
/// canonical edition's fingerprint so a converted file does not create a second
/// library entry. A direct lookup then misses, and read-along reports that no timing
/// data exists for a book that was in fact fully transcribed.
///
/// Timings and filters deliberately get different rules. A wrong transcript mistimes
/// a highlight, which is a visible annoyance. A wrong filter result would apply
/// another recording's timeline to this one and could play content a listener asked
/// never to hear, which is the one promise this product cannot break. So filters are
/// resolved only on a matching retail product identifier or a link the client
/// reported outright, never on inferred metadata similarity.
/// </remarks>
public sealed class EditionResolver(
    IPrivateTranscriptStore transcripts,
    IScanCatalog catalog,
    IEditionAliasStore aliases,
    IEditionSignatureStore signatures,
    ILogger<EditionResolver> logger) : IEditionResolver
{
    public async Task<PrivateTranscript?> LoadTranscript(
        BookFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        // 1. The fingerprint we were handed. Almost always the answer.
        var exact = await UsableTranscript(fingerprint, cancellationToken);
        if (exact is not null) return exact;

        // 2. A link recorded earlier, either by an import that reported its source
        //    file or by a previous run of step 3.
        foreach (var alias in aliases.Aliases(fingerprint))
        {
            var linked = await UsableTranscript(alias, cancellationToken);
            if (linked is null) continue;
            logger.LogInformation(
                "Resolved a transcript for {Title} through a recorded edition alias.",
                fingerprint.WorkTitle);
            return linked;
        }

        // 3. Compare edition evidence against every scanned fingerprint. Only
        //    reached on a miss, and the link is remembered so it stays a one-off.
        foreach (var candidate in MatchingCandidates(fingerprint))
        {
            var matched = await UsableTranscript(candidate, cancellationToken);
            if (matched is null) continue;

            aliases.Link(fingerprint, candidate);
            logger.LogInformation(
                "Matched {Title} to an existing scanned edition by evidence and linked them.",
                fingerprint.WorkTitle);
            return matched;
        }

        return null;
    }

    public ScanResult? FindResult(BookFingerprint fingerprint)
    {
        var exact = catalog.FindResult(fingerprint);
        if (exact is not null) return exact;

        var signature = signatures.Find(fingerprint);

        foreach (var alias in aliases.Aliases(fingerprint))
        {
            var linked = catalog.FindResult(alias);
            if (linked is null) continue;
            logger.LogInformation(
                "Reused filter results for {Title} through a recorded edition alias.",
                fingerprint.WorkTitle);
            return linked;
        }

        // Deliberately narrower than the transcript path. Filter timings decide what a listener
        // hears, so only evidence that identifies one recording is accepted here, never metadata
        // similarity: a shared title and author describe two different readings of one book just
        // as well as they describe the same recording twice.
        //
        // Two kinds qualify. A retail identifier names one published edition. Failing that, a
        // chapter structure of eight or more marks agreeing to the second is a pattern a
        // different reading does not share, and one a tagger does not type. The second kind is
        // new, and it is what lets a converted or re-tagged copy of an already scanned recording
        // find its filters instead of paying to scan the same audio again.
        var hasIdentifier = !string.IsNullOrWhiteSpace(signature?.ProductIdentifier);
        var hasStructure = signature?.ChapterOffsetSeconds is { Count: >= 8 };
        if (!hasIdentifier && !hasStructure) return null;

        foreach (var candidate in catalog.ListFingerprints())
        {
            if (SameKey(candidate, fingerprint)) continue;

            var candidateSignature = signatures.Find(candidate);
            var identifiersAgree = hasIdentifier &&
                !string.IsNullOrWhiteSpace(candidateSignature?.ProductIdentifier) &&
                EditionMatch.SameRecording(fingerprint, candidate, signature, candidateSignature);
            // Runtime stays required, as it is for every match, because it is the one claim a
            // re-tag cannot forge. File kind stays required too: a read-along EPUB attached
            // to this recording reports this recording's own runtime and chapter offsets by
            // design, which would otherwise satisfy every check below and hand its own
            // filter timings back for the text file that merely accompanies it.
            var structureAgrees =
                EditionMatch.ChapterStructureIdentifies(signature, candidateSignature) &&
                EditionMatch.SameRuntime(fingerprint, candidate) &&
                EditionMatch.SameFileKind(fingerprint, candidate);
            if (!identifiersAgree && !structureAgrees) continue;

            var matched = catalog.FindResult(candidate);
            if (matched is null) continue;

            aliases.Link(fingerprint, candidate);
            logger.LogInformation(
                "Reused filter results for {Title} after matching {Evidence}.",
                fingerprint.WorkTitle,
                identifiersAgree ? "retail product identifiers" : "chapter structure and runtime");
            return matched;
        }

        return null;
    }

    /// <summary>
    /// Known fingerprints that the evidence says are the same recording. Cheap and
    /// entirely in memory, so it runs before any transcript store read.
    /// </summary>
    private IEnumerable<BookFingerprint> MatchingCandidates(BookFingerprint fingerprint)
    {
        var signature = signatures.Find(fingerprint);
        foreach (var candidate in catalog.ListFingerprints())
        {
            if (SameKey(candidate, fingerprint)) continue;
            if (!EditionMatch.SameRecording(
                    fingerprint, candidate, signature, signatures.Find(candidate)))
            {
                continue;
            }
            yield return candidate;
        }
    }

    private static bool SameKey(BookFingerprint left, BookFingerprint right) =>
        InMemoryScanCatalog.FingerprintKey(left) == InMemoryScanCatalog.FingerprintKey(right);

    /// <summary>
    /// A transcript with no segments carries no timing, so it is not an answer.
    /// </summary>
    private async Task<PrivateTranscript?> UsableTranscript(
        BookFingerprint fingerprint,
        CancellationToken cancellationToken)
    {
        var transcript = await transcripts.Load(fingerprint, cancellationToken);
        return transcript is not null && transcript.Segments.Count > 0 ? transcript : null;
    }
}
