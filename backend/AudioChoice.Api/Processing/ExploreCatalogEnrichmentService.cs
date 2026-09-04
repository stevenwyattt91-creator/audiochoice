using AudioChoice.Api.Contracts;
using AudioChoice.Api.Services;

namespace AudioChoice.Api.Processing;

/// <summary>
/// Fills in cover art and descriptions for Explore entries missing either, on its own,
/// without an administrator having to notice and run a backfill by hand.
/// </summary>
/// <remarks>
/// Both used to be entirely manual: <c>/v1/admin/explore/descriptions/backfill</c> and a
/// deploy script that seeded artwork one hardcoded title at a time. Neither ran unless
/// someone remembered to trigger it, so a newly scanned or newly renamed edition -- every
/// title <see cref="KnownWorkCatalog"/> now cleans up on its own is exactly this case --
/// could sit with a blank cover indefinitely.
///
/// Runs on the same periodic-background-service shape as
/// <see cref="TemporaryAudioCleanupService"/> and <see cref="CompanionTransferCleanupService"/>,
/// for the same reason: nothing here is urgent enough to justify blocking a request on an
/// outside API, and a missed pass costs nothing because the next one tries the same entries
/// again. A cover or description that fails to resolve today is not remembered as a
/// permanent failure -- the calls are cheap and the alternative, a manual denylist an
/// administrator has to maintain, is worse than an occasional wasted lookup.
/// </remarks>
public sealed class ExploreCatalogEnrichmentService(
    IScanCatalog catalog,
    ICoverArtProvider covers,
    ISynopsisProvider synopses,
    ILogger<ExploreCatalogEnrichmentService> logger) : BackgroundService
{
    /// <summary>
    /// How often to sweep the catalogue for gaps.
    /// </summary>
    /// <remarks>
    /// Explore changes by scans and renames, not by the minute, and every entry considered
    /// costs an outside API call. An hour keeps a newly published book from waiting long
    /// while keeping this a background housekeeping task rather than a load generator.
    /// </remarks>
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Enrich(stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await Enrich(stoppingToken);
        }
    }

    private async Task Enrich(CancellationToken cancellationToken)
    {
        // Withheld and hidden entries are deliberately skipped: an edition nobody can see in
        // Explore is not worth spending an outside lookup on, and an unpublishable one -- no
        // title, no author -- usually has nothing a title search could even be built from.
        var entries = catalog.ListExploreCatalog()
            .Where(entry => entry.IsPublished && entry.IsPublishable)
            .ToArray();
        if (entries.Length == 0) return;

        // Read once rather than per entry: ListFingerprints is a full scan of every edition,
        // and this loop already runs it against every entry needing enrichment below.
        var fingerprintsByCatalogID = catalog.ListFingerprints()
            .GroupBy(value => value.Sha256[..Math.Min(24, value.Sha256.Length)].ToLowerInvariant())
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!fingerprintsByCatalogID.TryGetValue(entry.Book.CatalogID, out var fingerprint)) continue;

            var needsCover = string.IsNullOrWhiteSpace(entry.Book.CoverImageURL);
            var needsDescription = string.IsNullOrWhiteSpace(entry.Book.Description);
            if (!needsCover && !needsDescription) continue;

            if (needsCover) await FillCover(fingerprint, entry.Book, cancellationToken);
            if (needsDescription) await FillDescription(fingerprint, entry.Book, cancellationToken);
        }
    }

    private async Task FillCover(
        BookFingerprint fingerprint, ExploreCatalogBook book, CancellationToken cancellationToken)
    {
        try
        {
            var found = await covers.Find(
                book.Title, book.Author, book.ProductIdentifier, cancellationToken);
            if (found is null) return;
            if (catalog.SaveEditionCover(fingerprint, found.Value.Bytes, found.Value.ContentType))
            {
                logger.LogInformation("Stored a cover for {Title}.", book.Title);
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            logger.LogInformation(error, "Could not fetch a cover for {Title}.", book.Title);
        }
    }

    private async Task FillDescription(
        BookFingerprint fingerprint, ExploreCatalogBook book, CancellationToken cancellationToken)
    {
        try
        {
            var synopsis = await synopses.Find(fingerprint, book.ProductIdentifier, cancellationToken);
            if (synopsis is null) return;
            if (catalog.SaveEditionDescription(fingerprint, synopsis))
            {
                logger.LogInformation("Stored a description for {Title}.", book.Title);
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            logger.LogInformation(error, "Could not fetch a description for {Title}.", book.Title);
        }
    }
}
