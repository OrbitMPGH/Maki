using Mangarr.Api.Dtos;
using Mangarr.Api.Services;
using Mangarr.Core.Entities;
using Mangarr.Core.Metadata;
using Mangarr.Core.Naming;
using Mangarr.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Controllers;

[ApiController]
[Route("api/v1/series")]
public class SeriesController(
    MangarrDbContext db,
    IEnumerable<IMetadataProvider> metadataProviders,
    CoverService coverService,
    SourceMatchService sourceMatchService,
    ChapterSyncService chapterSyncService,
    ILogger<SeriesController> logger) : ControllerBase
{
    [HttpPost("{id:int}/refresh")]
    public async Task<IActionResult> Refresh(int id, CancellationToken ct)
    {
        if (!await db.Series.AnyAsync(s => s.Id == id, ct))
        {
            return NotFound();
        }

        var newChapters = await chapterSyncService.SyncSeriesAsync(id, ct);
        return Ok(new { newChapters });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var series = await db.Series.OrderBy(s => s.SortTitle).ToListAsync(ct);
        var chapterCounts = await db.Chapters
            .GroupBy(c => c.SeriesId)
            .Select(g => new { SeriesId = g.Key, Total = g.Count(), WithFile = g.Count(c => c.ChapterFileId != null) })
            .ToDictionaryAsync(x => x.SeriesId, ct);

        return Ok(series.Select(s =>
        {
            chapterCounts.TryGetValue(s.Id, out var counts);
            return SeriesDto.FromEntity(s, counts?.Total ?? 0, counts?.WithFile ?? 0);
        }));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct)
    {
        var series = await db.Series.FindAsync([id], ct);
        if (series is null)
        {
            return NotFound();
        }

        var total = await db.Chapters.CountAsync(c => c.SeriesId == id, ct);
        var withFile = await db.Chapters.CountAsync(c => c.SeriesId == id && c.ChapterFileId != null, ct);
        return Ok(SeriesDto.FromEntity(series, total, withFile));
    }

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] AddSeriesRequest request, CancellationToken ct)
    {
        var rootFolder = await db.RootFolders.FindAsync([request.RootFolderId], ct);
        if (rootFolder is null)
        {
            return BadRequest(new { error = "Root folder not found" });
        }

        var provider = metadataProviders.First();
        var metadata = await provider.GetAsync(request.MetadataProviderId, ct);
        if (metadata is null)
        {
            return BadRequest(new { error = "Series not found on metadata provider" });
        }

        if (metadata.MangaBakaId is int existingId &&
            await db.Series.AnyAsync(s => s.MangaBakaId == existingId, ct))
        {
            return Conflict(new { error = "Series already exists in library" });
        }

        var series = new Series
        {
            Title = metadata.Title,
            SortTitle = SortTitleFor(metadata.Title),
            OriginalTitle = metadata.OriginalTitle,
            Status = metadata.Status,
            Overview = metadata.Description,
            Year = metadata.Year,
            Genres = [.. metadata.Genres],
            Tags = [.. metadata.Tags],
            MangaBakaId = metadata.MangaBakaId,
            AniListId = metadata.AniListId,
            MalId = metadata.MalId,
            MangaUpdatesId = metadata.MangaUpdatesId,
            MangaDexUuid = metadata.MangaDexUuid,
            Monitored = request.Monitored,
            MonitorNewItems = Enum.TryParse<NewChapterMonitorMode>(request.MonitorNewItems, true, out var mode)
                ? mode
                : NewChapterMonitorMode.All,
            RootFolderId = rootFolder.Id,
            FolderName = FileNameSanitizer.Sanitize(metadata.Title),
            TotalChapters = metadata.TotalChapters,
            TotalVolumes = metadata.TotalVolumes,
            AuthorStory = metadata.AuthorStory,
            AuthorArt = metadata.AuthorArt,
            Added = DateTime.UtcNow,
            LastMetadataRefresh = DateTime.UtcNow
        };

        db.Series.Add(series);
        await db.SaveChangesAsync(ct);

        try
        {
            Directory.CreateDirectory(Path.Combine(rootFolder.Path, series.FolderName));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not create series folder for {Title}", series.Title);
        }

        if (metadata.CoverUrl != null)
        {
            var coverPath = await coverService.DownloadCoverAsync(series.Id, metadata.CoverUrl, ct);
            if (coverPath != null)
            {
                series.CoverPath = coverPath;
                await db.SaveChangesAsync(ct);
            }
        }

        // Link site sources by title match, then pull the initial chapter list.
        try
        {
            var mapped = await sourceMatchService.AutoMatchAsync(series, ct);
            if (mapped.Count > 0)
            {
                await chapterSyncService.SyncSeriesAsync(series.Id, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auto source matching failed for {Title}", series.Title);
        }

        return CreatedAtAction(nameof(Get), new { id = series.Id }, SeriesDto.FromEntity(series));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, [FromQuery] bool deleteFiles, CancellationToken ct)
    {
        var series = await db.Series.Include(s => s.RootFolder).FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null)
        {
            return NotFound();
        }

        if (deleteFiles && series.RootFolder != null)
        {
            var folder = Path.Combine(series.RootFolder.Path, series.FolderName);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }

        db.Series.Remove(series);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static string SortTitleFor(string title)
    {
        var lowered = title.ToLowerInvariant();
        foreach (var article in (string[])["the ", "a ", "an "])
        {
            if (lowered.StartsWith(article, StringComparison.Ordinal))
            {
                return lowered[article.Length..];
            }
        }

        return lowered;
    }
}
