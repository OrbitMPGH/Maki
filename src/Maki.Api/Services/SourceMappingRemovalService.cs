using Maki.Core.Entities;
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
    ChapterFileDeletion deletion,
    ILogger<SourceMappingRemovalService> logger)
{
    public async Task<SourceMappingRemovalResult?> RemoveAsync(
        int mappingId, bool deleteFiles, CancellationToken ct = default)
    {
        var seriesId = await db.SourceMappings
            .Where(m => m.Id == mappingId)
            .Select(m => (int?)m.SeriesId)
            .FirstOrDefaultAsync(ct);
        if (seriesId is null)
        {
            return null;
        }

        using var seriesLock = await SeriesLocks.SeriesAsync(seriesId.Value, ct);
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
        var toDelete = new List<ChapterFileDeletion.DiskTarget>();
        if (deleteFiles && detachedFileIds.Count > 0)
        {
            var files = await db.ChapterFiles
                .Where(f => detachedFileIds.Contains(f.Id))
                .ToListAsync(ct);
            var targets = mapping.Series.RootFolder is null
                ? files.Select(f => new ChapterFileDeletion.DiskTarget(f.RelativePath, null, false)).ToList()
                : await deletion.TargetsAsync(
                    mapping.Series.RootFolder.Path, files.Select(f => f.RelativePath), detachedFileIds.ToHashSet(), ct);
            foreach (var (file, target) in files.Zip(targets))
            {
                if (target.AbsolutePath is null)
                {
                    logger.LogWarning("Refusing to delete {File}: resolves outside the root or through a linked folder",
                        file.RelativePath);
                    failedFileDeletions.Add(file.RelativePath);
                    continue;
                }

                if (target.Claimed)
                {
                    logger.LogInformation("Kept {File} on disk: another record still points at it", target.AbsolutePath);
                }
                else
                {
                    toDelete.Add(target);
                }

                archives.Invalidate(file.Id);
                db.ChapterFiles.Remove(file);
            }
        }

        db.Chapters.RemoveRange(removed);
        db.SourceMappings.Remove(mapping);
        await db.SaveChangesAsync(ct);

        // Rows are committed first, so a failure here orphans a file for Health to find rather than
        // leaving a row behind for a file that is gone.
        var deletedFiles = 0;
        foreach (var target in toDelete)
        {
            if (deletion.DeleteFromDisk(target.AbsolutePath!))
            {
                deletedFiles++;
            }
            else
            {
                failedFileDeletions.Add(target.RelativePath);
            }
        }

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
            await batches.DiscardAsync(item.SeriesId, item.Id);

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
