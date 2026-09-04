using System.Text.Json;
using AudioChoice.Api.Contracts;

namespace AudioChoice.Api.Services;

/// <summary>
/// Looks up cover artwork for a book whose file carries none.
/// </summary>
public interface ICoverArtProvider
{
    /// <summary>
    /// Downloads a usable cover image for this edition, or null when none can be found.
    /// </summary>
    /// <param name="productIdentifier">
    /// The retail identifier the file reported, when it had one. An ISBN narrows an Open
    /// Library lookup to the exact edition; an Audible ASIN is not indexed by either
    /// provider below, so a title search remains the general case.
    /// </param>
    Task<(byte[] Bytes, string ContentType)?> Find(
        string title,
        string? author,
        string? productIdentifier,
        CancellationToken cancellationToken);
}

/// <summary>
/// Fetches cover artwork from the iTunes Search API, falling back to Open Library's cover
/// service when iTunes has nothing.
/// </summary>
/// <remarks>
/// iTunes is checked first because it indexes audiobooks specifically and its search takes a
/// free-text query, which is what a scanned file's own title and author actually are. Open
/// Library's cover service takes only an ISBN, which most files here do not carry (Audible
/// ASINs are not ISBNs and are not indexed by either provider), so it is the narrower,
/// second-choice path rather than the primary one. Neither requires an API key.
///
/// This mirrors <see cref="OpenLibrarySynopsisProvider"/> in shape and in when it runs: a
/// missing cover is not a failure worth surfacing to a listener, and the periodic backfill
/// that calls this simply tries again later.
/// </remarks>
public sealed class ITunesCoverArtProvider(
    HttpClient client,
    ILogger<ITunesCoverArtProvider> logger) : ICoverArtProvider
{
    /// <summary>
    /// The smallest artwork worth storing. iTunes returns a placeholder pixel for a query
    /// with no real match, and Open Library returns a tiny "no cover" stand-in under the
    /// same URL shape as a real one, so an implausibly small download is treated as a miss
    /// rather than stored and shown to every listener who opens that book.
    /// </summary>
    private const int MinimumUsableBytes = 2_000;

    /// <summary>Refused outright, matching the upload endpoints' own limit.</summary>
    public const int MaximumBytes = 2_000_000;

    /// <summary>Public for the same reason as <see cref="MaximumBytes"/>: tested directly.</summary>
    public const int MinimumBytes = MinimumUsableBytes;

    public async Task<(byte[] Bytes, string ContentType)?> Find(
        string title,
        string? author,
        string? productIdentifier,
        CancellationToken cancellationToken)
    {
        try
        {
            var fromITunes = await FindOnITunes(title, author, cancellationToken);
            if (fromITunes is not null) return fromITunes;

            var isbn = OpenLibrarySynopsisProvider.AsISBN(productIdentifier);
            return isbn is null ? null : await Download(
                $"https://covers.openlibrary.org/b/isbn/{isbn}-L.jpg?default=false", cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogInformation(error, "Could not look up cover artwork for {Title}.", title);
            return null;
        }
    }

    private async Task<(byte[] Bytes, string ContentType)?> FindOnITunes(
        string title, string? author, CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString(
            string.Join(' ', new[] { title, author }.Where(value => !string.IsNullOrWhiteSpace(value))));
        var response = await client.GetFromJsonAsync<JsonElement>(
            $"https://itunes.apple.com/search?term={query}&media=audiobook&entity=audiobook&country=US&limit=10",
            cancellationToken);
        if (!response.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // iTunes' own relevance ranking is trusted for which result is the right book; only
        // the artwork URL needs work. It hands back a 100x100 thumbnail by default, and the
        // catalogue needs a size worth displaying full-screen.
        foreach (var result in results.EnumerateArray())
        {
            var artwork = result.TryGetProperty("artworkUrl100", out var url) ? url.GetString() : null;
            if (string.IsNullOrWhiteSpace(artwork)) continue;
            foreach (var replacement in new[] { "1200x1200bb", "600x600bb" })
            {
                var upscaled = artwork.Replace("100x100bb", replacement, StringComparison.Ordinal);
                var downloaded = await Download(upscaled, cancellationToken);
                if (downloaded is not null) return downloaded;
            }
        }
        return null;
    }

    private async Task<(byte[] Bytes, string ContentType)?> Download(
        string url, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        var contentType = NormalizeContentType(response.Content.Headers.ContentType?.MediaType);
        if (contentType is null) return null;
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return bytes.Length is >= MinimumUsableBytes and <= MaximumBytes ? (bytes, contentType) : null;
    }

    /// <summary>
    /// Maps a provider's reported content type onto the three the storage endpoints accept,
    /// or null when it is something else entirely (an HTML error page, most often).
    /// </summary>
    public static string? NormalizeContentType(string? mediaType) => mediaType?.ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => "image/jpeg",
        "image/png" => "image/png",
        "image/webp" => "image/webp",
        _ => null,
    };
}
