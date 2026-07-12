using Mangarr.Api.Configuration;
using Mangarr.Api.Dtos;
using Mangarr.Api.Hubs;
using Mangarr.Core.ComicInfo;
using Mangarr.Core.Download;
using Mangarr.Core.Entities;
using Mangarr.Core.Naming;
using Mangarr.Core.Sources;
using Mangarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Services;

/// <summary>
/// Runs one queue item through the full pipeline:
/// fetch page URLs → download pages → validate → ComicInfo → CBZ → atomic import.
/// </summary>
public class ChapterDownloadProcessor(
    MangarrDbContext db,
    SourceRegistry sourceRegistry,
    PageDownloader pageDownloader,
    EventBroadcaster events,
    AppPaths paths,
    KavitaScanService kavitaScans,
    ILogger<ChapterDownloadProcessor> logger)
{
    public async Task ProcessAsync(int queueItemId, CancellationToken ct)
    {
        var item = await db.DownloadQueue
            .Include(q => q.SourceMapping)
            .Include(q => q.Chapter)
            .Include(q => q.Series)!.ThenInclude(s => s!.RootFolder)
            .FirstOrDefaultAsync(q => q.Id == queueItemId, ct);

        if (item is null || item.Status is QueueStatus.Completed or QueueStatus.Cancelled)
        {
            return;
        }

        if (item.Chapter is null)
        {
            // Torrent grabs are handled by CompletedDownloadJob, not the page pipeline.
            return;
        }

        var chapter = item.Chapter;
        var series = item.Series!;
        var rootFolder = series.RootFolder!;

        var workingDir = Path.Combine(paths.DownloadCacheDir, item.Id.ToString());

        try
        {
            // 1. Resolve the chapter on a source that actually has it. Start with the
            // assigned mapping, then fall back through the series' other enabled
            // mappings in priority order — sources rarely carry identical catalogs.
            await SetStatusAsync(item, QueueStatus.FetchingPages, ct);
            var (mapping, source, sourceChapterId) = await ResolveAcrossMappingsAsync(item, chapter, ct);

            if (item.SourceMappingId != mapping.Id)
            {
                item.SourceMappingId = mapping.Id;
                await db.SaveChangesAsync(ct);
            }

            var sourceChapter = new SourceChapter(
                mapping.SourceName, mapping.SourceSeriesId, sourceChapterId,
                chapter.NumberRaw, chapter.Number, chapter.Volume, chapter.Title,
                chapter.Language, chapter.ReleaseDate);
            var pages = await source.GetPagesAsync(sourceChapter, ct);

            if (pages.Pages.Count == 0)
            {
                await FailAsync(item, "Source returned no pages", ct);
                return;
            }

            item.PagesTotal = pages.Pages.Count;
            await SetStatusAsync(item, QueueStatus.Downloading, ct);

            // 2. Download pages (resumable — existing files are kept).
            var lastBroadcast = DateTime.MinValue;
            var pageFiles = await pageDownloader.DownloadAsync(pages, workingDir, async (done, _) =>
            {
                item.PagesDone = done;
                if (DateTime.UtcNow - lastBroadcast > TimeSpan.FromSeconds(1))
                {
                    lastBroadcast = DateTime.UtcNow;
                    await BroadcastAsync(item, chapter, series, mapping.SourceName);
                }
            }, ct);

            item.PagesDone = pages.Pages.Count;

            // 3. Validate images.
            await SetStatusAsync(item, QueueStatus.Validating, ct);
            foreach (var file in pageFiles)
            {
                if (!await ImageValidator.IsValidImageAsync(file, ct))
                {
                    File.Delete(file); // force re-download on retry
                    throw new InvalidOperationException($"Invalid image: {Path.GetFileName(file)}");
                }
            }

            // 4–5. ComicInfo + CBZ into a temp dir on the same volume as the library.
            await SetStatusAsync(item, QueueStatus.Packaging, ct);
            var comicInfo = ComicInfoBuilder.Serialize(ComicInfoBuilder.Build(series, chapter, pageFiles.Count));
            var tmpDir = Path.Combine(rootFolder.Path, ".mangarr", "tmp");
            var tmpCbz = Path.Combine(tmpDir, $"{item.Id}.cbz");
            CbzPackager.Package(pageFiles, comicInfo, tmpCbz);

            // 6. Atomic move into the library.
            await SetStatusAsync(item, QueueStatus.Importing, ct);
            var relativePath = FileNameBuilder.BuildRelativePath(series, chapter);
            var finalPath = Path.Combine(rootFolder.Path, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
            File.Move(tmpCbz, finalPath, overwrite: true);

            var chapterFile = new ChapterFile
            {
                SeriesId = series.Id,
                RelativePath = relativePath,
                Size = new FileInfo(finalPath).Length,
                SourceName = mapping.SourceName,
                DateAdded = DateTime.UtcNow
            };
            db.ChapterFiles.Add(chapterFile);
            await db.SaveChangesAsync(ct);

            chapter.ChapterFileId = chapterFile.Id;
            item.Status = QueueStatus.Completed;
            item.CompletedAt = DateTime.UtcNow;
            item.ErrorMessage = null;
            await db.SaveChangesAsync(ct);

            await BroadcastAsync(item, chapter, series, mapping.SourceName);
            await events.ChapterImported(series.Id, chapter.Id);
            kavitaScans.QueueScan(Path.Combine(rootFolder.Path, series.FolderName), series.Id);

            TryDeleteDirectory(workingDir);
            logger.LogInformation("Imported {Series} {Chapter} from {Source}",
                series.Title, chapter.Number, mapping.SourceName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // shutdown; startup recovery re-queues in-flight items
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Download failed for queue item {Id}", item.Id);
            await FailAsync(item, ex.Message, ct);
        }
    }

    private async Task<(SourceMapping Mapping, ISource Source, string SourceChapterId)> ResolveAcrossMappingsAsync(
        DownloadQueueItem item, Chapter chapter, CancellationToken ct)
    {
        var mappings = await db.SourceMappings
            .Where(m => m.SeriesId == chapter.SeriesId && m.Enabled)
            .OrderBy(m => m.Id == item.SourceMappingId ? -1 : m.Priority)
            .ToListAsync(ct);

        if (mappings.Count == 0)
        {
            throw new InvalidOperationException("Series has no enabled source mappings");
        }

        var errors = new List<string>();
        foreach (var mapping in mappings)
        {
            var source = sourceRegistry.Find(mapping.SourceName);
            if (source is null)
            {
                continue;
            }

            try
            {
                var sourceChapterId = await ResolveSourceChapterIdAsync(source, mapping, chapter, ct);
                if (sourceChapterId != null)
                {
                    return (mapping, source, sourceChapterId);
                }

                errors.Add($"{source.Name}: chapter not listed");
            }
            catch (Exception ex)
            {
                errors.Add($"{source.Name}: {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            $"Chapter {chapter.Number} unavailable on all sources ({string.Join("; ", errors)})");
    }

    /// <summary>
    /// The queue stores our Chapter, not the source's chapter id, so look it up
    /// in the source's current chapter list. Keeps the queue robust when a source
    /// re-uploads chapters under new ids.
    /// </summary>
    private static async Task<string?> ResolveSourceChapterIdAsync(
        ISource source, SourceMapping mapping, Chapter chapter, CancellationToken ct)
    {
        var chapters = await source.ListChaptersAsync(mapping.SourceSeriesId, mapping.LanguageFilter, ct);

        var match = chapter.Number is not null
            ? chapters.FirstOrDefault(c => c.Number == chapter.Number && c.Volume == chapter.Volume)
              ?? chapters.FirstOrDefault(c => c.Number == chapter.Number)
            : chapters.FirstOrDefault(c => c.Number is null &&
                string.Equals(c.Title, chapter.Title, StringComparison.OrdinalIgnoreCase));

        return match?.SourceChapterId;
    }

    private async Task SetStatusAsync(DownloadQueueItem item, QueueStatus status, CancellationToken ct)
    {
        item.Status = status;
        await db.SaveChangesAsync(ct);
        if (item.Series != null)
        {
            await BroadcastAsync(item, item.Chapter, item.Series, item.SourceMapping?.SourceName ?? "?");
        }
    }

    private async Task FailAsync(DownloadQueueItem item, string error, CancellationToken ct)
    {
        item.Status = QueueStatus.Failed;
        item.ErrorMessage = error;
        item.RetryCount++;
        await db.SaveChangesAsync(ct);
        if (item.Series != null)
        {
            await BroadcastAsync(item, item.Chapter, item.Series, item.SourceMapping?.SourceName ?? "?");
        }
    }

    private Task BroadcastAsync(DownloadQueueItem item, Chapter? chapter, Series series, string sourceName) =>
        events.QueueUpdated(QueueItemDto.FromEntity(item, chapter, series, sourceName));

    private void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not clean working dir {Dir}", dir);
        }
    }
}
