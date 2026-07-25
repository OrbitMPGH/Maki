using Maki.Api.Configuration;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Reading;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace Maki.Api.Controllers;

public record SaveProgressRequest(int PageIndex, bool? Completed);

/// <summary>A null spec clears the series override, falling back to the global defaults.</summary>
public record SeriesReaderPrefsRequest(ReaderPrefsSpec? Prefs);

/// <summary>
/// Serves pages out of the library's CBZ files and records what has been read.
/// <para>
/// Page requests are authenticated by the <c>?apikey=</c> query parameter that
/// <c>ApiKeyMiddleware</c> already accepts, because an <c>&lt;img&gt;</c> tag cannot send a
/// header. Deliberately no middleware carve-out like the one cover art has: covers are
/// thumbnails, whole pages are the content itself.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/reader")]
public class ReaderController(
    MakiDbContext db,
    ReaderService reader,
    SettingsService settings,
    KavitaReadImportService readImport,
    AppPaths paths,
    ILogger<ReaderController> logger) : ControllerBase
{
    private const int ThumbnailWidth = 200;

    /// <summary>The series' own reader settings if it has any, else the global defaults.</summary>
    private async Task<ReaderPrefsSpec> EffectivePrefsAsync(Series series, CancellationToken ct) =>
        series.ReaderPrefsJson is { Length: > 0 } own
            ? ReaderPrefsSpec.Parse(own)
            : ReaderPrefsSpec.Parse(await settings.GetAsync(SettingKeys.ReaderPrefs, ct));

    /// <summary>Sets or clears a series' reader override.</summary>
    [HttpPut("series/{seriesId:int}/prefs")]
    public async Task<IActionResult> SetSeriesPrefs(
        int seriesId, [FromBody] SeriesReaderPrefsRequest request, CancellationToken ct)
    {
        var series = await db.Series.FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        if (series is null)
        {
            return NotFound();
        }

        series.ReaderPrefsJson = request.Prefs is null ? null : ReaderPrefsSpec.Serialize(request.Prefs);
        await db.SaveChangesAsync(ct);
        return Ok(new { seriesId, prefs = request.Prefs, overridden = series.ReaderPrefsJson is not null });
    }

    [HttpGet("chapter/{id:int}")]
    public async Task<IActionResult> Manifest(int id, CancellationToken ct)
    {
        var slice = await reader.SliceAsync(id, ct);
        if (slice is null)
        {
            return NotFound(new { error = "Chapter has no readable file" });
        }

        var (previous, next) = await reader.NeighboursAsync(slice.Chapter, ct);
        var saved = await reader.ProgressAsync(id, ct);

        return Ok(new
        {
            chapterId = slice.Chapter.Id,
            seriesId = slice.Series.Id,
            seriesTitle = slice.Series.Title,
            label = ChapterLabel(slice.Chapter),
            number = slice.Chapter.Number,
            volume = slice.Chapter.Volume,
            language = slice.Chapter.Language,
            pageCount = slice.PageCount,
            resumePage = saved?.Completed == true ? 0 : saved?.PageIndex ?? 0,
            completed = saved?.Completed ?? false,
            previousChapterId = previous,
            nextChapterId = next,
            prefs = await EffectivePrefsAsync(slice.Series, ct),
            prefsOverridden = slice.Series.ReaderPrefsJson is not null
        });
    }

    [HttpGet("chapter/{id:int}/page/{page:int}")]
    public async Task<IActionResult> Page(int id, int page, CancellationToken ct)
    {
        var slice = await reader.SliceAsync(id, ct);
        if (slice is null || page < 0 || page >= slice.PageCount)
        {
            return NotFound();
        }

        var entry = slice.Pages[slice.StartPage + page];

        // The archive is immutable in practice, and the size guards against a re-import
        // reusing the id, so the response can be cached hard.
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

        // Range processing stays off (the default): the zip entry stream is forward-only.
        return File(stream, CbzReader.ContentType(entry), lastModified: null, entityTag: etag);
    }

    [HttpGet("chapter/{id:int}/thumb/{page:int}")]
    public async Task<IActionResult> Thumbnail(int id, int page, CancellationToken ct)
    {
        var slice = await reader.SliceAsync(id, ct);
        if (slice is null || page < 0 || page >= slice.PageCount)
        {
            return NotFound();
        }

        var absoluteIndex = slice.StartPage + page;
        var dir = Path.Combine(paths.ReaderCacheDir, slice.ChapterFileId.ToString());
        var cached = Path.Combine(dir, $"{slice.ArchiveSize}-{absoluteIndex}.jpg");
        Response.Headers.CacheControl = "private, max-age=31536000, immutable";

        if (!System.IO.File.Exists(cached))
        {
            var entry = slice.Pages[absoluteIndex];
            try
            {
                await using var source = CbzReader.OpenPage(slice.ArchivePath, entry);
                if (source is null)
                {
                    return NotFound();
                }

                using var image = await Image.LoadAsync(source, ct);
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(ThumbnailWidth, 0),
                    Mode = ResizeMode.Max
                }));

                Directory.CreateDirectory(dir);
                await image.SaveAsJpegAsync(cached, new JpegEncoder { Quality = 80 }, ct);
            }
            catch (Exception e)
            {
                // AVIF in particular cannot be decoded by the pinned ImageSharp build.
                logger.LogDebug(e, "Thumbnail failed for chapter {ChapterId} page {Page}", id, page);
                return NotFound();
            }
        }

        return PhysicalFile(cached, "image/jpeg");
    }

    [HttpPut("chapter/{id:int}/progress")]
    public async Task<IActionResult> SaveProgress(int id, [FromBody] SaveProgressRequest request,
        CancellationToken ct)
    {
        var slice = await reader.SliceAsync(id, ct);
        if (slice is null)
        {
            return NotFound();
        }

        var finished = await reader.SaveProgressAsync(slice, request.PageIndex, request.Completed, ct);
        return Ok(new { chapterId = id, pageIndex = request.PageIndex, completed = finished || request.Completed == true });
    }

    [HttpPost("chapter/{id:int}/read")]
    public async Task<IActionResult> MarkRead(int id, CancellationToken ct)
    {
        var slice = await reader.SliceAsync(id, ct);
        if (slice is null)
        {
            return NotFound();
        }

        await reader.SaveProgressAsync(slice, slice.PageCount - 1, completed: true, ct);
        return Ok(new { chapterId = id, completed = true });
    }

    [HttpPost("chapter/{id:int}/unread")]
    public async Task<IActionResult> MarkUnread(int id, CancellationToken ct)
    {
        await reader.ClearProgressAsync(id, ct);
        return Ok(new { chapterId = id, completed = false });
    }

    /// <summary>
    /// Whether the built-in reader has ever been used. The UI ORs this with "Kavita is
    /// configured" to decide whether to show read progress at all — the Kavita check alone used
    /// to be that gate, and on its own it would hide a reader-only user's progress.
    /// </summary>
    [HttpGet("used")]
    public async Task<IActionResult> Used(CancellationToken ct) =>
        Ok(new { used = await db.ChapterProgress.AnyAsync(ct) });

    /// <summary>
    /// Imports read status from Kavita. Runs in the background — a large library is one Kavita
    /// call per series — so this returns immediately and the UI polls <c>GET import/kavita</c>.
    /// </summary>
    [HttpPost("import/kavita")]
    public IActionResult StartKavitaImport() =>
        readImport.Start()
            ? Accepted(new { started = true })
            : Conflict(new { error = "An import is already running" });

    [HttpGet("import/kavita")]
    public IActionResult KavitaImportStatus() => Ok(new
    {
        running = readImport.State.Running,
        finishedAt = readImport.State.FinishedAt,
        result = readImport.State.Result,
        error = readImport.State.Error,
    });

    [HttpGet("chapter/{id:int}/bookmarks")]
    public async Task<IActionResult> Bookmarks(int id, CancellationToken ct) =>
        Ok(await db.ReaderBookmarks
            .Where(b => b.ChapterId == id)
            .OrderBy(b => b.PageIndex)
            .Select(b => new { b.Id, b.ChapterId, b.PageIndex, b.CreatedAt })
            .ToListAsync(ct));

    /// <summary>
    /// Adds or removes a bookmark on a page. Idempotent per (chapter, page), so a double-tap of
    /// the toolbar button toggles rather than stacking duplicates.
    /// </summary>
    [HttpPut("chapter/{id:int}/bookmark/{page:int}")]
    public async Task<IActionResult> ToggleBookmark(int id, int page, CancellationToken ct)
    {
        var chapter = await db.Chapters.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (chapter is null)
        {
            return NotFound();
        }

        var existing = await db.ReaderBookmarks
            .FirstOrDefaultAsync(b => b.ChapterId == id && b.PageIndex == page, ct);
        if (existing is not null)
        {
            db.ReaderBookmarks.Remove(existing);
            await db.SaveChangesAsync(ct);
            return Ok(new { chapterId = id, page, bookmarked = false });
        }

        db.ReaderBookmarks.Add(new ReaderBookmark
        {
            SeriesId = chapter.SeriesId,
            ChapterId = id,
            PageIndex = page,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return Ok(new { chapterId = id, page, bookmarked = true });
    }

    /// <summary>Per-chapter read state for a series, for the chapter table.</summary>
    [HttpGet("series/{seriesId:int}/progress")]
    public async Task<IActionResult> SeriesProgress(int seriesId, CancellationToken ct)
    {
        var rows = await db.ChapterProgress
            .Where(p => p.SeriesId == seriesId)
            .Select(p => new { p.ChapterId, p.PageIndex, p.PageCount, p.Completed, p.UpdatedAt })
            .ToListAsync(ct);
        return Ok(rows);
    }

    /// <summary>
    /// Where to resume: the most recently touched unfinished chapter, else the first
    /// downloaded chapter that has not been read.
    /// </summary>
    [HttpGet("series/{seriesId:int}/continue")]
    public async Task<IActionResult> Continue(int seriesId, CancellationToken ct)
    {
        var inProgress = await db.ChapterProgress
            .Where(p => p.SeriesId == seriesId && !p.Completed)
            .OrderByDescending(p => p.UpdatedAt)
            .FirstOrDefaultAsync(ct);
        if (inProgress is not null)
        {
            return Ok(new { chapterId = inProgress.ChapterId, page = inProgress.PageIndex });
        }

        var completed = (await db.ChapterProgress
                .Where(p => p.SeriesId == seriesId && p.Completed)
                .Select(p => p.ChapterId)
                .ToListAsync(ct))
            .ToHashSet();

        var next = (await db.Chapters
                .Where(c => c.SeriesId == seriesId && c.ChapterFileId != null)
                .Select(c => new { c.Id, c.Number, c.Volume })
                .ToListAsync(ct))
            .OrderBy(c => c.Number is null ? 1 : 0)
            .ThenBy(c => c.Number)
            .ThenBy(c => c.Volume)
            .ThenBy(c => c.Id)
            .FirstOrDefault(c => !completed.Contains(c.Id));

        return next is null
            ? NotFound(new { error = "Nothing left to read" })
            : Ok(new { chapterId = next.Id, page = 0 });
    }

    private static string ChapterLabel(Chapter chapter)
    {
        if (chapter.IsOneShot || chapter.Number is null)
        {
            return chapter.Title ?? "One-shot";
        }

        var number = chapter.Number.Value.ToString("0.###");
        return chapter.Volume is { } volume ? $"Vol.{volume} Ch.{number}" : $"Ch.{number}";
    }
}
