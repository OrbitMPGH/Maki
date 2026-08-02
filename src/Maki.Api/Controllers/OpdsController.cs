using Microsoft.AspNetCore.Authorization;
using Maki.Api.Auth;
using Maki.Api.Services;
using Maki.Core.Opds;
using Maki.Core.Reading;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
/// <para>
/// A path-borne secret is also a secret that lands in request logs, so <c>Program.cs</c> drops
/// this prefix out of Serilog's request logging and the redaction below is the only thing that
/// writes an OPDS URL to a log.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/opds/{token}")]
// Authenticated by the token in the route, checked by this controller on every action, because a
// reading app takes one feed URL and cannot be told to send a header or hold a cookie. This is a
// handover to a different mechanism, not a hole: the token resolves to a specific user's API key row,
// and a wrong or revoked one answers 404 — never 401, which would confirm the catalogue exists.
[AllowAnonymous]
public class OpdsController(
    OpdsCatalogService catalog,
    OpdsAccessService access,
    ReaderService reader,
    Maki.Data.MakiDbContext db,
    Maki.Api.Configuration.AppPaths paths,
    ILogger<OpdsController> logger) : ControllerBase
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Resolves the token in the path to the user it belongs to, or logs a redacted rejection and
    /// returns null. Callers turn a null into a 404.
    /// </summary>
    private async Task<OpdsAccess?> AuthorizeAsync(string token, CancellationToken ct)
    {
        var resolved = await access.ResolveAsync(token, ct);
        if (resolved is not null)
        {
            // The feed token is the credential, so this is where the request stops being anonymous.
            // CurrentUserMiddleware narrowed the scope to nobody (no cookie, no API key header) —
            // widening it to the token's owner here is what makes the catalogue show *their* library
            // and their progress, and it must happen before any query runs.
            db.Scope.SetUser(resolved.UserId, resolved.AllRootFolders);
            return resolved;
        }

        // Never log the path as-is: the token is in it. Logged at all because "my reader says the
        // catalogue doesn't exist" is otherwise undebuggable — a single reason string, though,
        // because the caller gets an undifferentiated 404 and the log should not be the place that
        // distinguishes "disabled" from "revoked token" for anyone reading over a shoulder.
        logger.LogWarning("OPDS request rejected: {Method} /api/v1/opds/<token>{Rest}",
            Request.Method,
            RemainderAfterToken());

        return null;
    }

    /// <summary>
    /// Series artwork, under the token.
    /// <para>
    /// Reading apps render a feed with thumbnails and hold exactly one credential — the feed URL — so
    /// covers have to be reachable with it. They used to come off <c>/api/v1/mediacover</c>, which was
    /// anonymous; now that it requires a session, this is where a reader gets them.
    /// </para>
    /// <para>
    /// The series is resolved through EF first, exactly as <c>MediaCoverController</c> does. The path
    /// is built from a caller-supplied id, so without a query the <c>Series</c> root-folder filter —
    /// which <see cref="AuthorizeAsync"/> just narrowed the scope for — never runs, and a token whose
    /// owner holds one root folder could walk every cover on the instance. A series the caller cannot
    /// see answers 404 rather than 403, so the endpoint does not confirm which ids exist.
    /// </para>
    /// </summary>
    [HttpGet("cover/{seriesId:int}")]
    public async Task<IActionResult> Cover(string token, int seriesId, CancellationToken ct)
    {
        if (await AuthorizeAsync(token, ct) is null)
        {
            return NotFound();
        }

        if (!await db.Series.AnyAsync(s => s.Id == seriesId, ct))
        {
            return NotFound();
        }

        var path = Path.Combine(paths.MediaCoverDir, seriesId.ToString(), "cover.jpg");
        return System.IO.File.Exists(path) ? PhysicalFile(path, "image/jpeg") : NotFound();
    }

    /// <summary>The request path with the token segment removed, safe to log.</summary>
    private string RemainderAfterToken()
    {
        var path = Request.Path.Value ?? string.Empty;
        const string prefix = "/api/v1/opds/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var slash = path.IndexOf('/', prefix.Length);
        return slash < 0 ? string.Empty : path[slash..];
    }

    private ContentResult Feed(OpdsFeed feed) => new()
    {
        Content = OpdsXml.Render(feed),
        ContentType = OpdsXml.ContentTypeFor(feed.Kind),
        StatusCode = StatusCodes.Status200OK,
    };

    [HttpGet("")]
    public async Task<IActionResult> Root(string token, CancellationToken ct) =>
        await AuthorizeAsync(token, ct) is null
            ? NotFound()
            : Feed(catalog.Root(Context(token)));

    [HttpGet("series")]
    public async Task<IActionResult> SeriesList(string token, [FromQuery] int page = 0,
        CancellationToken ct = default) =>
        await AuthorizeAsync(token, ct) is null
            ? NotFound()
            : Feed(await catalog.SeriesFeedAsync(Context(token), Math.Max(0, page), ct));

    [HttpGet("series/{seriesId:int}")]
    public async Task<IActionResult> SeriesChapters(string token, int seriesId, [FromQuery] int page = 0,
        CancellationToken ct = default)
    {
        if (await AuthorizeAsync(token, ct) is null)
        {
            return NotFound();
        }

        var feed = await catalog.ChaptersFeedAsync(Context(token), seriesId, Math.Max(0, page), ct);
        return feed is null ? NotFound() : Feed(feed);
    }

    [HttpGet("recent")]
    public async Task<IActionResult> Recent(string token, CancellationToken ct) =>
        await AuthorizeAsync(token, ct) is null
            ? NotFound()
            : Feed(await catalog.RecentFeedAsync(Context(token), ct));

    [HttpGet("on-deck")]
    public async Task<IActionResult> OnDeck(string token, CancellationToken ct) =>
        await AuthorizeAsync(token, ct) is null
            ? NotFound()
            : Feed(await catalog.OnDeckFeedAsync(Context(token), ct));

    /// <summary>
    /// The OpenSearch description the root feed's <c>rel="search"</c> link points at. Readers fetch
    /// this to learn the query template; without it their search box stays greyed out.
    /// </summary>
    [HttpGet("search.xml")]
    public async Task<IActionResult> SearchDescription(string token, CancellationToken ct)
    {
        if (await AuthorizeAsync(token, ct) is null)
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
        await AuthorizeAsync(token, ct) is null
            ? NotFound()
            : Feed(await catalog.SearchFeedAsync(Context(token), q ?? string.Empty, Math.Max(0, page), ct));

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
        if (await AuthorizeAsync(token, ct) is null)
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
        if (await AuthorizeAsync(token, ct) is not { } settings)
        {
            return NotFound();
        }

        var slice = await reader.SliceAsync(chapterId, ct);
        if (slice is null || page < 0 || page >= slice.PageCount)
        {
            return NotFound();
        }

        // Deliberately before the 304 below, not after: a reader holding the page in its own cache
        // still revalidates, and that revalidation is the only evidence Maki gets that the page is
        // being looked at. Recording after the early return would make a resumed chapter stop
        // reporting progress exactly where the reader already has pages in hand.
        if (settings.TrackProgress)
        {
            await RecordPageAsync(slice, page, ct);
        }

        var entry = slice.Pages[slice.StartPage + page];
        var etag = new EntityTagHeaderValue($"\"{slice.ChapterFileId}-{slice.ArchiveSize}-{slice.StartPage + page}\"");
        if (Request.GetTypedHeaders().IfNoneMatch?.Any(t => t.Compare(etag, useStrongComparison: false)) == true)
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        // Immutable and long-lived, matching the built-in reader: the archive does not change, and
        // the size in the ETag guards against a re-import reusing the id. Worth knowing alongside
        // progress tracking — a reader that re-reads from its own cache without revalidating
        // reports nothing, so a re-read only starts registering again at the first page it has to
        // actually fetch. Harmless in practice: completion is sticky, so nothing is lost, and the
        // resume position catches up as soon as the reader moves past what it cached.
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
    /// Records a streamed page as progress. The completion rule — and the one place it deviates
    /// from the built-in reader's — lives in <see cref="OpdsProgressPolicy"/>.
    /// </summary>
    private async Task RecordPageAsync(ReaderService.ChapterSlice slice, int page, CancellationToken ct)
    {
        try
        {
            var existing = await reader.ProgressAsync(slice.Chapter.Id, ct);
            var completed = OpdsProgressPolicy.CompletionFor(existing is not null, page, slice.PageCount);
            await reader.SaveProgressAsync(slice, page, completed, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Progress is a side effect of serving the page; never fail the page over it. Client
            // disconnects are excluded so an abandoned prefetch isn't logged as a problem.
            logger.LogWarning(e, "OPDS progress write failed for chapter {ChapterId} page {Page}",
                slice.Chapter.Id, page);
        }
    }

    private OpdsContext Context(string token) => new(Request.PathBase.Value ?? string.Empty, token);

    private static string SanitizeFileName(string name) =>
        string.Concat(name.Select(c => InvalidFileNameChars.Contains(c) ? '_' : c));
}
