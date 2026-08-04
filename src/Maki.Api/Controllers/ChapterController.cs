using Microsoft.AspNetCore.Authorization;
using Maki.Api.Auth;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Parsing;
using Maki.Core.Paths;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

public record LinkChaptersRequest(int[] ChapterIds, string RelativePath);

public record SetChaptersMonitoredRequest(int[] ChapterIds, bool Monitored);

[ApiController]
[Route("api/v1/chapter")]
public class ChapterController(
    MakiDbContext db,
    DownloadQueueService queue,
    StatsEventService stats,
    ReaderArchiveCache archives,
    ILogger<ChapterController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int seriesId, CancellationToken ct)
    {
        var rows = await db.Chapters
            .Where(c => c.SeriesId == seriesId)
            .Include(c => c.ChapterFile)
            .OrderBy(c => c.Number == null ? 1 : 0).ThenBy(c => c.Number).ThenBy(c => c.Volume)
            .Select(c => new
            {
                c.Id,
                c.SeriesId,
                c.Number,
                c.NumberRaw,
                c.Volume,
                c.Title,
                c.IsOneShot,
                c.Language,
                c.ReleaseDate,
                c.Monitored,
                HasFile = c.ChapterFileId != null,
                FilePath = c.ChapterFile != null ? c.ChapterFile.RelativePath : null
            })
            .ToListAsync(ct);

        // When a chapter's backing file is a volume/compilation CBZ, surface that
        // volume so the UI can show "Vol.x Ch.y" even for scrape-source chapters that
        // carry no volume metadata (parsing can't run inside the EF query, so it's
        // done here in memory over the materialized rows).
        var chapters = rows.Select(c => new
        {
            c.Id,
            c.SeriesId,
            c.Number,
            c.NumberRaw,
            c.Volume,
            c.Title,
            c.IsOneShot,
            c.Language,
            c.ReleaseDate,
            c.Monitored,
            c.HasFile,
            c.FilePath,
            FileVolume = VolumeFileLabel(c.FilePath)
        });

        return Ok(chapters);
    }

    /// <summary>The volume label ("3", "1-2") of a backing file when it is a volume compilation, else null.</summary>
    public static string? VolumeFileLabel(string? relativePath)
    {
        if (relativePath is null)
        {
            return null;
        }

        var parsed = ReleaseNameParser.ParseFileName(relativePath);
        if (!parsed.IsVolume)
        {
            return null;
        }

        return parsed.VolumeEnd is { } end && end != parsed.Volume
            ? $"{parsed.Volume}-{end}"
            : parsed.Volume!.Value.ToString();
    }

    [Authorize(Policy = Policies.EditMetadata)]
    [HttpPut("{id:int}/monitor")]
    public async Task<IActionResult> SetMonitored(int id, [FromQuery] bool monitored, CancellationToken ct)
    {
        var chapter = await db.Chapters.FindAsync([id], ct);
        if (chapter is null)
        {
            return NotFound();
        }

        chapter.Monitored = monitored;
        await db.SaveChangesAsync(ct);
        return Ok(new { chapter.Id, chapter.Monitored });
    }

    /// <summary>Sets the monitored flag on a batch of chapters, for the Chapters table's select mode.</summary>
    [Authorize(Policy = Policies.EditMetadata)]
    [HttpPut("monitor")]
    public async Task<IActionResult> SetMonitoredBulk(
        [FromBody] SetChaptersMonitoredRequest request,
        CancellationToken ct)
    {
        if (request.ChapterIds.Length == 0)
        {
            return BadRequest(new { error = "No chapters selected" });
        }

        var chapters = await db.Chapters
            .Where(c => request.ChapterIds.Contains(c.Id))
            .ToListAsync(ct);

        foreach (var chapter in chapters)
        {
            chapter.Monitored = request.Monitored;
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { updated = chapters.Count });
    }

    /// <summary>
    /// Bulk-links chapters to a specific file in the series folder — for compilation CBZs
    /// or oddly-named files the automatic linker (<see cref="CbzLinkService"/>) couldn't
    /// match. Creates the backing <see cref="ChapterFile"/> record if the file was never
    /// imported (e.g. an "unrecognized" file surfaced by <c>GET /series/{id}/files</c>).
    /// </summary>
    [Authorize(Policy = Policies.EditMetadata)]
    [HttpPut("link")]
    public async Task<IActionResult> Link([FromBody] LinkChaptersRequest request, CancellationToken ct)
    {
        if (request.ChapterIds.Length == 0)
        {
            return BadRequest(new { error = "No chapters selected" });
        }

        var chapters = await db.Chapters.Where(c => request.ChapterIds.Contains(c.Id)).ToListAsync(ct);
        if (chapters.Count != request.ChapterIds.Length)
        {
            return NotFound();
        }

        var seriesId = chapters[0].SeriesId;
        if (chapters.Any(c => c.SeriesId != seriesId))
        {
            return BadRequest(new { error = "Chapters belong to different series" });
        }

        var series = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        if (series?.RootFolder is null)
        {
            return BadRequest(new { error = "Series has no root folder" });
        }

        // This is the one place a request-supplied path becomes a stored ChapterFile.RelativePath,
        // and every consumer of that column (delete, ComicInfo rewrite, the reader) joins it back
        // onto the root folder. A bare Path.Combine accepts "..\.." and discards the root outright
        // for an absolute argument, so an EditMetadata holder could point a row at maki.db and a
        // DeleteSeries holder could then delete it. Resolve is the containment check; reject rather
        // than sanitize, so nothing escaping ever reaches the database.
        var absPath = LibraryPaths.Resolve(series.RootFolder.Path, request.RelativePath);
        if (absPath is null)
        {
            return BadRequest(new { error = "Path is outside the series' root folder" });
        }

        if (!System.IO.File.Exists(absPath))
        {
            return BadRequest(new { error = "File not found on disk" });
        }

        var file = await db.ChapterFiles
            .FirstOrDefaultAsync(f => f.SeriesId == seriesId && f.RelativePath == request.RelativePath, ct);
        if (file is null)
        {
            file = new ChapterFile
            {
                SeriesId = seriesId,
                RelativePath = request.RelativePath,
                Size = new FileInfo(absPath).Length,
                SourceName = "Manual",
                DateAdded = DateTime.UtcNow
            };
            db.ChapterFiles.Add(file);
            stats.Record(StatsEventType.ChapterDownloaded, series.Id, series.Title);
            await db.SaveChangesAsync(ct);
        }

        foreach (var chapter in chapters)
        {
            chapter.ChapterFileId = file.Id;
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { fileId = file.Id, linked = chapters.Count });
    }

    /// <summary>Clears the file link on the given chapters, leaving them missing/unlinked.</summary>
    [Authorize(Policy = Policies.EditMetadata)]
    [HttpPut("unlink")]
    public async Task<IActionResult> Unlink([FromBody] int[] chapterIds, CancellationToken ct)
    {
        var chapters = await db.Chapters.Where(c => chapterIds.Contains(c.Id)).ToListAsync(ct);
        foreach (var chapter in chapters)
        {
            chapter.ChapterFileId = null;
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { unlinked = chapters.Count });
    }

    /// <summary>
    /// Permanently removes chapter rows — not just their file link — for cases like a
    /// broken auto-match that pulled in the wrong show: chapter data is otherwise
    /// additive-only, so bad rows would sit in the library forever. Also deletes the
    /// backing CBZ from disk when this batch drops the last chapter referencing it
    /// (a volume CBZ can back several chapters).
    /// </summary>
    [Authorize(Policy = Policies.DeleteSeries)]
    [HttpDelete]
    public async Task<IActionResult> Delete([FromBody] int[] chapterIds, CancellationToken ct)
    {
        if (chapterIds.Length == 0)
        {
            return BadRequest(new { error = "No chapters selected" });
        }

        var chapters = await db.Chapters.Where(c => chapterIds.Contains(c.Id)).ToListAsync(ct);
        if (chapters.Count == 0)
        {
            return Ok(new { deleted = 0 });
        }

        var seriesId = chapters[0].SeriesId;
        // The root folder below comes from this one series, so a mixed batch would delete series B's
        // file using series A's root path. Same check Link makes, for the same reason.
        if (chapters.Any(c => c.SeriesId != seriesId))
        {
            return BadRequest(new { error = "Chapters belong to different series" });
        }

        var series = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        var deletingIds = chapters.Select(c => c.Id).ToHashSet();

        var fileIds = chapters
            .Where(c => c.ChapterFileId != null)
            .Select(c => c.ChapterFileId!.Value)
            .Distinct()
            .ToList();

        foreach (var fileId in fileIds)
        {
            var stillReferenced = await db.Chapters
                .AnyAsync(c => c.ChapterFileId == fileId && !deletingIds.Contains(c.Id), ct);
            if (stillReferenced)
            {
                continue;
            }

            var file = await db.ChapterFiles.FindAsync([fileId], ct);
            if (file is null)
            {
                continue;
            }

            // Never File.Delete a bare Combine: a row written before the check in Link, or by any
            // future path that skips it, would delete whatever it points at outside the library.
            var absPath = series?.RootFolder is null
                ? null
                : LibraryPaths.Resolve(series.RootFolder.Path, file.RelativePath);
            if (series?.RootFolder is not null && absPath is null)
            {
                logger.LogWarning("Refusing to delete {File}: resolves outside {Root}",
                    file.RelativePath, series.RootFolder.Path);
            }

            if (absPath is not null)
            {
                try
                {
                    System.IO.File.Delete(absPath);
                }
                catch (DirectoryNotFoundException)
                {
                    // Containing directory is already gone — the file is effectively deleted.
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Could not delete {File}, removing records anyway", file.RelativePath);
                }
            }

            archives.Invalidate(file.Id);
            db.ChapterFiles.Remove(file);
        }

        db.Chapters.RemoveRange(chapters);
        await db.SaveChangesAsync(ct);
        return Ok(new { deleted = chapters.Count });
    }

    [Authorize(Policy = Policies.DownloadChapters)]
    [HttpPost("{id:int}/search")]
    public async Task<IActionResult> Search(int id, CancellationToken ct)
    {
        try
        {
            var item = await queue.EnqueueChapterAsync(id, ct);
            return item is null
                ? Conflict(new { error = "Chapter is already queued" })
                : Ok(new { queueItemId = item.Id });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
