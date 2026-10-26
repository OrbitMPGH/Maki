using Maki.Api.Configuration;
using Maki.Api.Komga;
using Maki.Api.Services;
using Maki.Core.Download;
using Maki.Core.Images;
using Maki.Core.Opds;
using Maki.Core.Reading;
using Maki.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

namespace Maki.Api.Controllers;

/// <summary>
/// A Komga-compatible API under <c>/komga</c>, so apps that only speak Komga (the Mihon Komga
/// extension and tracker, Tachimanga) can read from Maki and sync progress both ways. The server
/// address those apps take is <c>http://host:port/komga</c>; they append <c>/api/v1/...</c>
/// themselves, which is why this cannot live under Maki's own <c>/api/v1</c>.
/// <para>
/// Read-only apart from reading progress, which goes through <see cref="ReaderService"/> exactly
/// as the built-in reader's does, so it reaches stats, scrobbling and Kavita push-back, and a fully
/// incognito series stays out of them. Page fetches are not progress here, unlike OPDS: every Komga
/// client that tracks reading says so explicitly.
/// </para>
/// </summary>
[ApiController]
[Route("komga/api")]
// Authenticated by KomgaAuthFilter on every action, with the user's OPDS token, because a reading
// app holds that token and nothing else. Off unless the owner turned on both OPDS and the Komga API.
[AllowAnonymous]
[ServiceFilter(typeof(KomgaAuthFilter))]
public class KomgaController(
    KomgaCatalogService catalog,
    ReaderService reader,
    MakiDbContext db,
    AppPaths paths) : ControllerBase
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 1000;

    private OpdsAccess Access => (OpdsAccess)HttpContext.Items[typeof(OpdsAccess)]!;

    [HttpGet("v1/users/me")]
    [HttpGet("v2/users/me")]
    public async Task<IActionResult> Me(CancellationToken ct) =>
        Ok(await catalog.UserAsync(Access.UserId, Access.AllRootFolders, ct));

    /// <summary>Komga's way to trade a header credential for its session cookie. The filter already set it.</summary>
    [HttpGet("v1/login/set-cookie")]
    public IActionResult SetCookie() => NoContent();

    [HttpGet("v1/libraries")]
    public async Task<IActionResult> Libraries(CancellationToken ct) => Ok(await catalog.LibrariesAsync(ct));

    [HttpGet("v1/libraries/{id}")]
    public async Task<IActionResult> Library(string id, CancellationToken ct) =>
        int.TryParse(id, out var libraryId) && await catalog.LibraryAsync(libraryId, ct) is { } library
            ? Ok(library)
            : NotFound();

    [HttpGet("v1/series")]
    public async Task<IActionResult> Series(
        [FromQuery] string? search,
        [FromQuery(Name = "library_id")] string[]? libraryId,
        [FromQuery(Name = "read_status")] string[]? readStatus,
        [FromQuery] string[]? status,
        [FromQuery] string[]? genre,
        [FromQuery] string[]? tag,
        [FromQuery] string[]? publisher,
        [FromQuery] string[]? author,
        [FromQuery] string[]? sort,
        [FromQuery] int page = 0,
        [FromQuery] int? size = null,
        [FromQuery] bool unpaged = false,
        CancellationToken ct = default) =>
        Ok(await catalog.SeriesAsync(
            new KomgaSeriesQuery(search, Ids(libraryId), Values(readStatus), Values(status), Values(genre),
                Values(tag), Values(publisher), author, sort?.FirstOrDefault()),
            PageNumber(page), PageSize(size, unpaged), ct));

    [HttpGet("v1/series/new")]
    public Task<IActionResult> NewSeries(
        [FromQuery(Name = "library_id")] string[]? libraryId, [FromQuery] int page = 0, [FromQuery] int? size = null,
        [FromQuery] bool unpaged = false, CancellationToken ct = default) =>
        SortedSeries(libraryId, "createdDate,desc", page, size, unpaged, ct);

    [HttpGet("v1/series/updated")]
    [HttpGet("v1/series/latest")]
    public Task<IActionResult> UpdatedSeries(
        [FromQuery(Name = "library_id")] string[]? libraryId, [FromQuery] int page = 0, [FromQuery] int? size = null,
        [FromQuery] bool unpaged = false, CancellationToken ct = default) =>
        SortedSeries(libraryId, "lastModifiedDate,desc", page, size, unpaged, ct);

    private async Task<IActionResult> SortedSeries(
        string[]? libraryId, string sort, int page, int? size, bool unpaged, CancellationToken ct) =>
        Ok(await catalog.SeriesAsync(new KomgaSeriesQuery(LibraryIds: Ids(libraryId), Sort: sort),
            PageNumber(page), PageSize(size, unpaged), ct));

    [HttpGet("v1/series/{id}")]
    public async Task<IActionResult> OneSeries(string id, CancellationToken ct) =>
        int.TryParse(id, out var seriesId) && await catalog.OneSeriesAsync(seriesId, ct) is { } series
            ? Ok(series)
            : NotFound();

    [HttpGet("v1/series/{id}/thumbnail")]
    public async Task<IActionResult> SeriesThumbnail(string id, CancellationToken ct)
    {
        if (!int.TryParse(id, out var seriesId) || !await db.Series.Active().AnyAsync(s => s.Id == seriesId, ct))
        {
            return NotFound();
        }

        return (IActionResult?)Cover(seriesId) ?? NotFound();
    }

    [HttpGet("v1/series/{id}/books")]
    public async Task<IActionResult> SeriesBooks(
        string id,
        [FromQuery(Name = "read_status")] string[]? readStatus,
        [FromQuery] string[]? sort,
        [FromQuery] int page = 0,
        [FromQuery] int? size = null,
        [FromQuery] bool unpaged = false,
        CancellationToken ct = default)
    {
        if (!int.TryParse(id, out var seriesId))
        {
            return NotFound();
        }

        var books = await catalog.SeriesBooksAsync(
            seriesId, Values(readStatus), sort?.FirstOrDefault(), PageNumber(page), PageSize(size, unpaged), ct);
        return books is null ? NotFound() : Ok(books);
    }

    /// <summary>Marks every book in the series read. Silent, like the app's own bulk mark-read.</summary>
    [HttpPost("v1/series/{id}/read-progress")]
    public async Task<IActionResult> MarkSeriesRead(string id, CancellationToken ct)
    {
        if (!int.TryParse(id, out var seriesId) || await catalog.SeriesChapterIdsAsync(seriesId, ct) is not { } ids)
        {
            return NotFound();
        }

        await reader.MarkReadAsync(ids, ct);
        return NoContent();
    }

    [HttpDelete("v1/series/{id}/read-progress")]
    public async Task<IActionResult> MarkSeriesUnread(string id, CancellationToken ct)
    {
        if (!int.TryParse(id, out var seriesId) || await catalog.SeriesChapterIdsAsync(seriesId, ct) is not { } ids)
        {
            return NotFound();
        }

        await reader.ClearProgressAsync(ids, ct);
        return NoContent();
    }

    [HttpGet("v1/series/{id}/read-progress/tachiyomi")]
    public async Task<IActionResult> TachiyomiProgress(string id, CancellationToken ct) =>
        int.TryParse(id, out var seriesId) && await catalog.TachiyomiProgressV1Async(seriesId, ct) is { } progress
            ? Ok(progress)
            : NotFound();

    [HttpGet("v2/series/{id}/read-progress/tachiyomi")]
    public async Task<IActionResult> TachiyomiProgressV2(string id, CancellationToken ct) =>
        int.TryParse(id, out var seriesId) && await catalog.TachiyomiProgressAsync(seriesId, ct) is { } progress
            ? Ok(progress)
            : NotFound();

    [HttpPut("v1/series/{id}/read-progress/tachiyomi")]
    public async Task<IActionResult> SetTachiyomiProgress(
        string id, [FromBody] KomgaTachiyomiUpdate request, CancellationToken ct)
    {
        if (!int.TryParse(id, out var seriesId) || request.LastBookRead is not { } count)
        {
            return BadRequest();
        }

        if (await catalog.SeriesChapterIdsAsync(seriesId, ct) is null)
        {
            return NotFound();
        }

        return await catalog.NumberSortOfBookAsync(seriesId, count, ct) is { } upTo
            ? await ApplyTrackerProgressAsync(seriesId, upTo, ct)
            : NoContent();
    }

    [HttpPut("v2/series/{id}/read-progress/tachiyomi")]
    public async Task<IActionResult> SetTachiyomiProgressV2(
        string id, [FromBody] KomgaTachiyomiUpdateV2 request, CancellationToken ct) =>
        int.TryParse(id, out var seriesId) && request.LastBookNumberSortRead is { } upTo
            ? await ApplyTrackerProgressAsync(seriesId, upTo, ct)
            : BadRequest();

    /// <summary>
    /// "Read up to <paramref name="upTo"/>", from the Mihon tracker. Only ever marks read, as Komga
    /// does. One newly crossed chapter is a read the user just finished and is recorded as one,
    /// through the reader's own save; a jump of several is a sync (binding the tracker to a series
    /// already read in Mihon, catching up after offline reading) and is marked silently, so it does
    /// not land in Rewind as a day's reading.
    /// </summary>
    private async Task<IActionResult> ApplyTrackerProgressAsync(int seriesId, double upTo, CancellationToken ct)
    {
        if (await catalog.NewlyReadAsync(seriesId, upTo, ct) is not { } groups)
        {
            return NotFound();
        }

        if (groups.Count == 1)
        {
            // Several books share a group when they share a numberSort: the same chapter in several
            // languages, or a volume's chapter 0 sorted after higher numbers, which repeats the previous
            // value. The first readable one is the read; the others are ticked off with it, silently, and
            // so is every copy when none can be opened, rather than leaving the chapter unread.
            var rest = groups[0];
            foreach (var chapterId in groups[0])
            {
                if (await reader.SliceAsync(chapterId, ct) is { } slice)
                {
                    await reader.SaveProgressAsync(slice, slice.PageCount - 1, true, ReaderService.TimeReport.None, ct);
                    rest = rest.Where(id => id != chapterId).ToList();
                    break;
                }
            }

            await reader.MarkReadAsync(rest, ct);
        }
        else if (groups.Count > 1)
        {
            await reader.MarkReadAsync(groups.SelectMany(g => g).ToList(), ct);
        }

        return NoContent();
    }

    [HttpGet("v1/books")]
    public async Task<IActionResult> Books(
        [FromQuery] string? search,
        [FromQuery(Name = "library_id")] string[]? libraryId,
        [FromQuery(Name = "read_status")] string[]? readStatus,
        [FromQuery] string[]? sort,
        [FromQuery] int page = 0,
        [FromQuery] int? size = null,
        [FromQuery] bool unpaged = false,
        CancellationToken ct = default) =>
        Ok(await catalog.BooksAsync(
            new KomgaBookQuery(search, Ids(libraryId), Values(readStatus), sort?.FirstOrDefault()),
            PageNumber(page), PageSize(size, unpaged) ?? MaxPageSize, ct));

    [HttpGet("v1/books/latest")]
    public async Task<IActionResult> LatestBooks(
        [FromQuery(Name = "library_id")] string[]? libraryId, [FromQuery] int page = 0, [FromQuery] int? size = null,
        [FromQuery] bool unpaged = false, CancellationToken ct = default) =>
        Ok(await catalog.BooksAsync(new KomgaBookQuery(LibraryIds: Ids(libraryId), Sort: "createdDate,desc"),
            PageNumber(page), PageSize(size, unpaged) ?? MaxPageSize, ct));

    [HttpGet("v1/books/ondeck")]
    public async Task<IActionResult> OnDeck(
        [FromQuery] int page = 0, [FromQuery] int? size = null, [FromQuery] bool unpaged = false,
        CancellationToken ct = default) =>
        Ok(await catalog.OnDeckAsync(PageNumber(page), PageSize(size, unpaged), ct));

    [HttpGet("v1/books/{id}")]
    public async Task<IActionResult> Book(string id, CancellationToken ct) =>
        int.TryParse(id, out var chapterId) && await catalog.BookAsync(chapterId, ct) is { } book
            ? Ok(book)
            : NotFound();

    [HttpGet("v1/books/{id}/next")]
    public async Task<IActionResult> NextBook(string id, CancellationToken ct) =>
        int.TryParse(id, out var chapterId) && await catalog.NeighbourAsync(chapterId, next: true, ct) is { } book
            ? Ok(book)
            : NotFound();

    [HttpGet("v1/books/{id}/previous")]
    public async Task<IActionResult> PreviousBook(string id, CancellationToken ct) =>
        int.TryParse(id, out var chapterId) && await catalog.NeighbourAsync(chapterId, next: false, ct) is { } book
            ? Ok(book)
            : NotFound();

    /// <summary>The series cover, falling back to the book's first page.</summary>
    [HttpGet("v1/books/{id}/thumbnail")]
    public async Task<IActionResult> BookThumbnail(string id, CancellationToken ct)
    {
        if (!int.TryParse(id, out var chapterId))
        {
            return NotFound();
        }

        var seriesId = await db.Chapters.Active()
            .Where(c => c.Id == chapterId && c.ChapterFileId != null)
            .Select(c => (int?)c.SeriesId)
            .FirstOrDefaultAsync(ct);
        if (seriesId is null)
        {
            return NotFound();
        }

        return (IActionResult?)Cover(seriesId.Value) ?? await PageAsync(chapterId, 1, TranscodeFormat.Jpeg, ct);
    }

    /// <summary>The chapter's archive. A volume archive backing several chapters is served whole, as over OPDS.</summary>
    [HttpGet("v1/books/{id}/file")]
    public async Task<IActionResult> Download(string id, CancellationToken ct)
    {
        if (!int.TryParse(id, out var chapterId) || !await catalog.ServesChapterAsync(chapterId, ct) ||
            await reader.SliceAsync(chapterId, ct) is not { } slice)
        {
            return NotFound();
        }

        var name = $"{slice.Series.Title} - {ChapterLabel.For(slice.Chapter)}{Path.GetExtension(slice.ArchivePath)}";
        return PhysicalFile(slice.ArchivePath, OpdsXml.MimeType(slice.ArchivePath),
            string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)),
            enableRangeProcessing: true);
    }

    [HttpGet("v1/books/{id}/pages")]
    public async Task<IActionResult> Pages(string id, CancellationToken ct) =>
        int.TryParse(id, out var chapterId) && await catalog.PagesAsync(chapterId, ct) is { } pages
            ? Ok(pages)
            : NotFound();

    /// <summary>
    /// One page, 1-based as Komga numbers them. <c>?convert=jpeg</c> or <c>png</c> is what a Komga
    /// client sends for a page whose media type it cannot decode (an AVIF page on an older device, a
    /// WebP page in the Komga web reader on an old browser); the page is re-encoded unless its bytes
    /// already are that format. Any other value is a 400, as on Komga.
    /// </summary>
    [HttpGet("v1/books/{id}/pages/{number:int}")]
    [HttpGet("v1/books/{id}/pages/{number:int}/thumbnail")]
    public async Task<IActionResult> Page(string id, int number, [FromQuery] string? convert = null,
        CancellationToken ct = default)
    {
        if (!int.TryParse(id, out var chapterId))
        {
            return NotFound();
        }

        TranscodeFormat? target;
        switch (convert?.Trim().ToLowerInvariant())
        {
            case null or "":
                target = null;
                break;
            case "jpeg" or "jpg":
                target = TranscodeFormat.Jpeg;
                break;
            case "png":
                target = TranscodeFormat.Png;
                break;
            default:
                return BadRequest();
        }

        return await PageAsync(chapterId, number, target, ct);
    }

    [HttpPatch("v1/books/{id}/read-progress")]
    public async Task<IActionResult> SetBookProgress(
        string id, [FromBody] KomgaReadProgressUpdate request, CancellationToken ct)
    {
        if (request.Page is null && request.Completed is null)
        {
            return BadRequest();
        }

        if (!int.TryParse(id, out var chapterId) || !await catalog.ServesChapterAsync(chapterId, ct) ||
            await reader.SliceAsync(chapterId, ct) is not { } slice)
        {
            return NotFound();
        }

        if (request.Completed == true)
        {
            await reader.SaveProgressAsync(slice, slice.PageCount - 1, true, ReaderService.TimeReport.None, ct);
        }
        else if (request.Page is { } page)
        {
            // Null leaves the reader's own rule in charge, "the last page means finished", which is
            // Komga's too; an explicit false never un-reads a finished chapter, DELETE does that.
            await reader.SaveProgressAsync(slice, ReaderService.ClampPage(page - 1, slice.PageCount),
                request.Completed, ReaderService.TimeReport.None, ct);
        }

        return NoContent();
    }

    [HttpDelete("v1/books/{id}/read-progress")]
    public async Task<IActionResult> ClearBookProgress(string id, CancellationToken ct)
    {
        if (!int.TryParse(id, out var chapterId) ||
            !await catalog.ServesChapterAsync(chapterId, ct))
        {
            return NotFound();
        }

        await reader.ClearProgressAsync(chapterId, ct);
        return NoContent();
    }

    [HttpGet("v1/genres")]
    public async Task<IActionResult> Genres([FromQuery(Name = "library_id")] string? libraryId, CancellationToken ct) =>
        Ok(await catalog.GenresAsync(OneId(libraryId), ct));

    [HttpGet("v1/tags")]
    [HttpGet("v1/tags/series")]
    public async Task<IActionResult> Tags([FromQuery(Name = "library_id")] string? libraryId, CancellationToken ct) =>
        Ok(await catalog.TagsAsync(OneId(libraryId), ct));

    [HttpGet("v1/publishers")]
    public async Task<IActionResult> Publishers([FromQuery(Name = "library_id")] string? libraryId, CancellationToken ct) =>
        Ok(await catalog.PublishersAsync(OneId(libraryId), ct));

    [HttpGet("v1/authors")]
    public async Task<IActionResult> Authors(CancellationToken ct) => Ok(await catalog.AuthorsAsync(ct));

    [HttpGet("v2/authors")]
    public async Task<IActionResult> AuthorsPaged(
        [FromQuery] string? search, [FromQuery] int page = 0, [FromQuery] int? size = null,
        [FromQuery] bool unpaged = false, CancellationToken ct = default)
    {
        var authors = await catalog.AuthorsAsync(ct);
        if (search is { Length: > 0 })
        {
            authors = authors.Where(a => a.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return Ok(KomgaPage<KomgaAuthor>.Of(authors, PageNumber(page), PageSize(size, unpaged)));
    }

    /// <summary>Maki has no collections or read lists; Komga clients list them, so they get none.</summary>
    [HttpGet("v1/collections")]
    [HttpGet("v1/readlists")]
    public IActionResult EmptyList() => Ok(KomgaPage<object>.Of([], 0, DefaultPageSize));

    /// <summary>
    /// Anything else under <c>/komga</c> is a Komga endpoint Maki does not serve. Answered here so a
    /// client gets a 404 rather than the SPA's index page from the fallback route.
    /// </summary>
    [HttpGet("{**rest}", Order = int.MaxValue)]
    public IActionResult Unsupported() => NotFound();

    private async Task<IActionResult> PageAsync(int chapterId, int number, TranscodeFormat? target, CancellationToken ct)
    {
        var slice = await catalog.ServesChapterAsync(chapterId, ct) ? await reader.PageSliceAsync(chapterId, ct) : null;
        var index = number - 1;
        if (slice is null || index < 0 || index >= slice.PageCount)
        {
            return NotFound();
        }

        var entry = slice.Pages[slice.StartPage + index];
        var suffix = target switch { TranscodeFormat.Jpeg => "-jpeg", TranscodeFormat.Png => "-png", _ => "" };
        var etag = new EntityTagHeaderValue($"\"{slice.ChapterFileId}-{slice.ArchiveVersion}-{slice.StartPage + index}{suffix}\"");
        if (Request.GetTypedHeaders().IfNoneMatch?.Any(t => t.Compare(etag, useStrongComparison: false)) == true)
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        Response.Headers.CacheControl = "private, no-cache";
        string? rendered = null;
        if (ComicFile.IsPdf(slice.ArchivePath))
        {
            rendered = await ReaderPageCache.GetOrRenderFullPageAsync(paths, slice, slice.StartPage + index, entry, ct);
            if (rendered is null)
            {
                return NotFound();
            }

            if (target is null)
            {
                return PhysicalFile(rendered, CbzReader.ContentType(entry), lastModified: null, entityTag: etag);
            }
        }

        var stream = rendered is null ? await reader.OpenPageAsync(slice, entry, ct) : System.IO.File.OpenRead(rendered);
        if (stream is null)
        {
            return NotFound();
        }

        if (target is not { } format)
        {
            return File(stream, CbzReader.ContentType(entry), lastModified: null, entityTag: etag);
        }

        return await ConvertedAsync(stream, format, etag, ct);
    }

    /// <summary>
    /// The page as <paramref name="format"/>. Judged by its bytes, not its name: a page already in that
    /// format goes out untouched. 415 when no decoder Maki allows can read it (a BMP page), which is
    /// what Komga answers a conversion it cannot do with too, rather than bytes the client just said
    /// it cannot show.
    /// </summary>
    private async Task<IActionResult> ConvertedAsync(Stream stream, TranscodeFormat format, EntityTagHeaderValue etag,
        CancellationToken ct)
    {
        byte[] bytes;
        await using (stream)
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            bytes = buffer.ToArray();
        }

        var wanted = format == TranscodeFormat.Png ? "image/png" : "image/jpeg";
        if (ImageValidator.SniffMediaType(bytes) == wanted)
        {
            return File(bytes, wanted, lastModified: null, entityTag: etag);
        }

        try
        {
            var converted = await ImageWorkGate.RunAsync(() => Task.FromResult(ImageCodec.Transcode(bytes, format)), ct);
            return File(converted, wanted, lastModified: null, entityTag: etag);
        }
        catch (NetVips.VipsException)
        {
            return StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }
    }

    private PhysicalFileResult? Cover(int seriesId)
    {
        var path = Path.Combine(paths.MediaCoverDir, seriesId.ToString(), "cover.jpg");
        if (!System.IO.File.Exists(path))
        {
            return null;
        }

        var info = new FileInfo(path);
        var etag = new EntityTagHeaderValue($"\"{seriesId}-{info.Length}-{info.LastWriteTimeUtc.Ticks}\"");
        Response.Headers.CacheControl = "private, max-age=3600";
        return PhysicalFile(path, "image/jpeg", lastModified: info.LastWriteTimeUtc, entityTag: etag);
    }

    private static int? PageSize(int? size, bool unpaged) =>
        unpaged ? null : Math.Clamp(size ?? DefaultPageSize, 1, MaxPageSize);

    // Capped so page times size cannot wrap past int.MaxValue into a negative offset, which reads as 0.
    private static int PageNumber(int page) => Math.Clamp(page, 0, int.MaxValue / MaxPageSize);

    /// <summary>Komga takes a list either repeated or comma-separated, and clients use both.</summary>
    private static List<string>? Values(string[]? raw) =>
        raw is null
            ? null
            : raw.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToList();

    private static List<int>? Ids(string[]? raw) =>
        Values(raw)?.Select(v => int.TryParse(v, out var id) ? id : -1).ToList();

    private static int? OneId(string? raw) => int.TryParse(raw, out var id) ? id : null;
}
