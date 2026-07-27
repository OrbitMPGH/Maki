using System.Security.Cryptography;
using System.Text;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Opds;
using Maki.Core.Reading;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Maki.Api.Controllers;

/// <summary>
/// Serves the library as an OPDS 1.2 catalogue so reading apps — Panels, Chunky, KOReader, the
/// Mihon/Tachiyomi OPDS extensions — can read straight from Maki with no Kavita in between.
/// <para>
/// <b>Authentication is the token in the path</b>, not the instance API key and not a header.
/// Reading apps overwhelmingly accept a single feed URL and nothing else, and the API key would
/// hand whichever app it is pasted into the entire management API; <c>opds.token</c> is separate
/// and rotatable on its own. <c>ApiKeyMiddleware</c> therefore carves this prefix out, and every
/// action below re-checks the token itself — the carve-out is not an open door.
/// </para>
/// <para>
/// Everything answers <b>404</b> rather than 401 when OPDS is disabled or the token is wrong: a
/// disabled catalogue should not confirm that it exists, and a wrong token should not distinguish
/// itself from a wrong path.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/opds/{token}")]
public class OpdsController(
    OpdsCatalogService catalog,
    ReaderService reader,
    SettingsService settings,
    ILogger<OpdsController> logger) : ControllerBase
{
    /// <summary>
    /// True when OPDS is on and <paramref name="token"/> is the configured one. Compared in fixed
    /// time: the token sits in a URL that is guessed at, not typed once, so the usual argument for
    /// not bothering doesn't hold.
    /// </summary>
    private async Task<bool> AuthorizedAsync(string token, CancellationToken ct)
    {
        if (await settings.GetAsync(SettingKeys.OpdsEnabled, ct) != "true")
        {
            return false;
        }

        var expected = await settings.GetAsync(SettingKeys.OpdsToken, ct);
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(token))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected));
    }

    private ContentResult Feed(OpdsFeed feed) => new()
    {
        Content = OpdsXml.Render(feed),
        ContentType = OpdsXml.ContentTypeFor(feed.Kind),
        StatusCode = StatusCodes.Status200OK,
    };

    [HttpGet("")]
    public async Task<IActionResult> Root(string token, CancellationToken ct) =>
        await AuthorizedAsync(token, ct)
            ? Feed(catalog.Root(Context(token)))
            : NotFound();

    [HttpGet("series")]
    public async Task<IActionResult> SeriesList(string token, [FromQuery] int page = 0,
        CancellationToken ct = default) =>
        await AuthorizedAsync(token, ct)
            ? Feed(await catalog.SeriesFeedAsync(Context(token), Math.Max(0, page), ct))
            : NotFound();

    [HttpGet("series/{seriesId:int}")]
    public async Task<IActionResult> SeriesChapters(string token, int seriesId, [FromQuery] int page = 0,
        CancellationToken ct = default)
    {
        if (!await AuthorizedAsync(token, ct))
        {
            return NotFound();
        }

        var feed = await catalog.ChaptersFeedAsync(Context(token), seriesId, Math.Max(0, page), ct);
        return feed is null ? NotFound() : Feed(feed);
    }

    [HttpGet("recent")]
    public async Task<IActionResult> Recent(string token, CancellationToken ct) =>
        await AuthorizedAsync(token, ct)
            ? Feed(await catalog.RecentFeedAsync(Context(token), ct))
            : NotFound();

    [HttpGet("on-deck")]
    public async Task<IActionResult> OnDeck(string token, CancellationToken ct) =>
        await AuthorizedAsync(token, ct)
            ? Feed(await catalog.OnDeckFeedAsync(Context(token), ct))
            : NotFound();

    /// <summary>
    /// The OpenSearch description the root feed's <c>rel="search"</c> link points at. Readers fetch
    /// this to learn the query template; without it their search box stays greyed out.
    /// </summary>
    [HttpGet("search.xml")]
    public async Task<IActionResult> SearchDescription(string token, CancellationToken ct)
    {
        if (!await AuthorizedAsync(token, ct))
        {
            return NotFound();
        }

        return new ContentResult
        {
            Content = OpdsXml.RenderOpenSearch(
                "Maki", "Search the Maki library", $"{Context(token).Base}/search?q={{searchTerms}}"),
            ContentType = OpdsXml.OpenSearchType,
            StatusCode = StatusCodes.Status200OK,
        };
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search(string token, [FromQuery] string? q, [FromQuery] int page = 0,
        CancellationToken ct = default) =>
        await AuthorizedAsync(token, ct)
            ? Feed(await catalog.SearchFeedAsync(Context(token), q ?? string.Empty, Math.Max(0, page), ct))
            : NotFound();

    /// <summary>
    /// The chapter's CBZ, for readers that download rather than stream.
    /// <para>
    /// When a volume archive backs several chapters this serves the <em>whole</em> archive, not the
    /// chapter's slice of it. Re-zipping a slice per request would be the alternative, and handing
    /// over a superset is both cheaper and recoverable — the reader shows extra pages rather than
    /// silently missing some, which is the same trade <c>ReaderService.SliceBounds</c> makes.
    /// </para>
    /// </summary>
    [HttpGet("chapter/{chapterId:int}/file")]
    public async Task<IActionResult> Download(string token, int chapterId, CancellationToken ct)
    {
        if (!await AuthorizedAsync(token, ct))
        {
            return NotFound();
        }

        var slice = await reader.SliceAsync(chapterId, ct);
        if (slice is null)
        {
            return NotFound();
        }

        var name = $"{slice.Series.Title} - {ChapterLabel.For(slice.Chapter)}.cbz";
        return PhysicalFile(slice.ArchivePath, OpdsXml.ComicBookType, SanitizeFileName(name), enableRangeProcessing: true);
    }

    /// <summary>
    /// One page, for OPDS-PSE streaming. <paramref name="page"/> is zero-based and relative to the
    /// chapter's slice, matching both the PSE spec and the built-in reader's own page endpoint.
    /// <para>
    /// Fetching a page also records reading progress (unless <c>opds.trackprogress</c> is off),
    /// through the same <see cref="ReaderService.SaveProgressAsync"/> every other reader path uses
    /// — which is what makes an OPDS read show up in the library, in Rewind and at the trackers.
    /// PSE has no write-back call, so the page fetch is the only signal there is.
    /// </para>
    /// </summary>
    [HttpGet("chapter/{chapterId:int}/page/{page:int}")]
    public async Task<IActionResult> Page(string token, int chapterId, int page, CancellationToken ct)
    {
        if (!await AuthorizedAsync(token, ct))
        {
            return NotFound();
        }

        var slice = await reader.SliceAsync(chapterId, ct);
        if (slice is null || page < 0 || page >= slice.PageCount)
        {
            return NotFound();
        }

        if (await settings.GetAsync(SettingKeys.OpdsTrackProgress, ct) != "false")
        {
            await RecordPageAsync(slice, page, ct);
        }

        var entry = slice.Pages[slice.StartPage + page];
        var etag = new EntityTagHeaderValue($"\"{slice.ChapterFileId}-{slice.ArchiveSize}-{slice.StartPage + page}\"");
        if (Request.GetTypedHeaders().IfNoneMatch?.Any(t => t.Compare(etag, useStrongComparison: false)) == true)
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        Response.Headers.CacheControl = "private, max-age=31536000, immutable";

        var stream = CbzReader.OpenPage(slice.ArchivePath, entry);
        if (stream is null)
        {
            return NotFound();
        }

        // Range processing stays off: the zip entry stream is forward-only.
        return File(stream, CbzReader.ContentType(entry), lastModified: null, entityTag: etag);
    }

    /// <summary>
    /// Records a streamed page as progress.
    /// <para>
    /// The one deviation from the built-in reader: a request for the <em>last</em> page of a
    /// chapter that has no progress row yet is stored explicitly as not-complete. Several readers
    /// fetch the final page up front to size their page bar, and the reader's own rule ("at the
    /// last page means finished") would mark the whole chapter read before a word of it was — which
    /// is a sticky flag that also fires a read event at the trackers. Once any earlier page has
    /// been seen the normal rule applies again.
    /// </para>
    /// </summary>
    private async Task RecordPageAsync(ReaderService.ChapterSlice slice, int page, CancellationToken ct)
    {
        try
        {
            var existing = await reader.ProgressAsync(slice.Chapter.Id, ct);
            var jumpedStraightToTheEnd = existing is null && page >= slice.PageCount - 1;
            await reader.SaveProgressAsync(slice, page, jumpedStraightToTheEnd ? false : null, ct);
        }
        catch (Exception e)
        {
            // Progress is a side effect of serving the page; never fail the page over it.
            logger.LogWarning(e, "OPDS progress write failed for chapter {ChapterId} page {Page}",
                slice.Chapter.Id, page);
        }
    }

    private OpdsContext Context(string token) => new(Request.PathBase.Value ?? string.Empty, token);

    private static string SanitizeFileName(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
