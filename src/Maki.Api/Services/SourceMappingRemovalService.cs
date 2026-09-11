using Maki.Core.Entities;
using Maki.Core.Paths;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public record MissingChapterSnapshot(int MappingId, string SourceName);

public sealed class MissingChapterSnapshotsException(IReadOnlyList<MissingChapterSnapshot> mappings)
    : Exception("Refresh chapters once before removing this source")
{
    public IReadOnlyList<MissingChapterSnapshot> Mappings { get; } = mappings;
}

public record SourceMappingRemovalResult(
    int RemovedChapters,
    int RetainedChapters,
    int DetachedFiles,
    int DeletedFiles,
    int FailedFileDeletions,
    IReadOnlyList<string> FailedFileDeletionPaths);

/// <summary>
/// Removes a mapping and reconciles the series exclusively from stored, last-successful source
/// snapshots. No source request is made here, so several mappings can be removed consecutively
/// without repeatedly listing the same sites.
/// </summary>
public class SourceMappingRemovalService(
    MakiDbContext db,
    SourceAvailability sourceAvailability,
    DownloadQueueService queue,
    DownloadBatchNotifier batches,
    ReaderArchiveCache archives,
    ILogger<SourceMappingRemovalService> logger)
{
    public async Task<SourceMappingRemovalResult?> RemoveAsync(
        int mappingId, bool deleteFiles, CancellationToken ct = default)
    {
        var mapping = await db.SourceMappings
            .Include(m => m.Series!)
            .ThenInclude(s => s.RootFolder)
            .FirstOrDefaultAsync(m => m.Id == mappingId, ct);
        if (mapping?.Series is null)
        {
            return null;
        }

        var disabledSources = await sourceAvailability.DisabledAsync(ct);
        var remaining = await db.SourceMappings
            .Where(m => m.SeriesId == mapping.SeriesId && m.Id != mappingId && m.Enabled &&
                        !disabledSources.Contains(m.SourceName))
            .OrderBy(m => m.Priority)
            .ThenBy(m => m.Id)
            .ToListAsync(ct);

        var missing = remaining
            .Where(m => m.ChapterSnapshotAt is null)
            .Select(m => new MissingChapterSnapshot(m.Id, m.SourceName))
            .ToList();
        if (missing.Count > 0)
        {
            throw new MissingChapterSnapshotsException(missing);
        }

        var remainingIds = remaining.Select(m => m.Id).ToList();
        var supportedIds = remainingIds.Count == 0
            ? []
            : (await db.ChapterSourceLinks
                .Where(l => remainingIds.Contains(l.SourceMappingId))
                .Select(l => l.ChapterId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();

        var chapters = await db.Chapters
            .Where(c => c.SeriesId == mapping.SeriesId)
            .Include(c => c.ChapterFile)
            .ToListAsync(ct);
        var removed = chapters.Where(c => !supportedIds.Contains(c.Id)).ToList();
        var retained = chapters.Where(c => supportedIds.Contains(c.Id)).ToList();

        await RebuildMetadataAsync(retained, remainingIds, ct);

        // A chapter number can be valid on both the wrong and correct series. Its row survives,
        // but a CBZ acquired through the mapping being removed must not remain readable as if it
        // were the correct content.
        var wrongSourceFileIds = chapters
            .Where(c => c.ChapterFile is not null &&
                        string.Equals(c.ChapterFile.SourceName, mapping.SourceName,
                            StringComparison.OrdinalIgnoreCase))
            .Select(c => c.ChapterFileId!.Value)
            .ToHashSet();
        foreach (var chapter in retained.Where(c => c.ChapterFileId is { } fileId && wrongSourceFileIds.Contains(fileId)))
        {
            chapter.ChapterFileId = null;
        }

        var affectedFileIds = removed
            .Where(c => c.ChapterFileId is not null)
            .Select(c => c.ChapterFileId!.Value)
            .Concat(wrongSourceFileIds)
            .ToHashSet();
        var stillReferencedFileIds = retained
            .Where(c => c.ChapterFileId is not null)
            .Select(c => c.ChapterFileId!.Value)
            .ToHashSet();
        var detachedFileIds = affectedFileIds.Except(stillReferencedFileIds).ToList();

        await CancelAffectedQueueItemsAsync(mapping, removed, ct);

        var failedFileDeletions = new List<string>();
        var deletedFiles = 0;
        if (deleteFiles && detachedFileIds.Count > 0)
        {
            var files = await db.ChapterFiles
                .Where(f => detachedFileIds.Contains(f.Id))
                .ToListAsync(ct);
            foreach (var file in files)
            {
                var path = mapping.Series.RootFolder is null
                    ? null
                    : LibraryPaths.Resolve(mapping.Series.RootFolder.Path, file.RelativePath);
                if (path is null)
                {
                    logger.LogWarning("Refusing to delete {File}: path is outside the series root", file.RelativePath);
                    failedFileDeletions.Add(file.RelativePath);
                    continue;
                }

                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Could not delete detached file {File}", file.RelativePath);
                    failedFileDeletions.Add(file.RelativePath);
                    continue;
                }

                archives.Invalidate(file.Id);
                db.ChapterFiles.Remove(file);
                deletedFiles++;
            }
        }

        db.Chapters.RemoveRange(removed);
        db.SourceMappings.Remove(mapping);
        await db.SaveChangesAsync(ct);

        return new SourceMappingRemovalResult(
            removed.Count,
            retained.Count,
            detachedFileIds.Count,
            deletedFiles,
            failedFileDeletions.Count,
            failedFileDeletions);
    }

    private async Task RebuildMetadataAsync(
        IReadOnlyCollection<Chapter> chapters,
        IReadOnlyCollection<int> remainingMappingIds,
        CancellationToken ct)
    {
        if (chapters.Count == 0)
        {
            return;
        }

        var chapterIds = chapters.Select(c => c.Id).ToList();
        var links = await db.ChapterSourceLinks
            .AsNoTracking()
            .Where(l => chapterIds.Contains(l.ChapterId) && remainingMappingIds.Contains(l.SourceMappingId))
            .Include(l => l.SourceMapping)
            .ToListAsync(ct);
        var byChapter = links.ToLookup(l => l.ChapterId);

        foreach (var chapter in chapters)
        {
            var preferred = byChapter[chapter.Id]
                .OrderBy(l => l.SourceMapping!.Priority)
                .ThenBy(l => l.SourceMappingId)
                .ToList();

            chapter.NumberRaw = preferred.Select(l => l.NumberRaw).FirstOrDefault(v => v is not null);
            chapter.Volume = preferred.Select(l => l.Volume).FirstOrDefault(v => v is not null);
            chapter.Title = preferred.Select(l => l.Title).FirstOrDefault(v => v is not null);
            chapter.ReleaseDate = preferred.Select(l => l.ReleaseDate).FirstOrDefault(v => v is not null);
        }
    }

    private async Task CancelAffectedQueueItemsAsync(
        SourceMapping mapping,
        IReadOnlyCollection<Chapter> removedChapters,
        CancellationToken ct)
    {
        var removedIds = removedChapters.Select(c => c.Id).ToList();
        var active = await db.DownloadQueue
            .Where(q => q.Status != QueueStatus.Completed && q.Status != QueueStatus.Cancelled &&
                        (q.SourceMappingId == mapping.Id ||
                         (q.ChapterId != null && removedIds.Contains(q.ChapterId.Value))))
            .ToListAsync(ct);

        foreach (var item in active)
        {
            queue.CancelWork(item.Id);
            batches.Discard(item.SeriesId, item.Id);

            if (item.Status is QueueStatus.Queued or QueueStatus.Failed or QueueStatus.RateLimited or QueueStatus.Resolving)
            {
                db.DownloadQueue.Remove(item);
            }
            else
            {
                item.Status = QueueStatus.Cancelled;
            }
        }
    }
}
