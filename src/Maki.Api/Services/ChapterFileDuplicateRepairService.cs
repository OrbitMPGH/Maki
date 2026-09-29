using Maki.Core.Entities;
using Maki.Core.Paths;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// One-time merge of <see cref="ChapterFile"/> rows that name the same file twice.
/// <para>
/// A torrent import whose link step ran more than once for the same placed file (a poll cut off
/// after the rows were saved but before the queue item was, or a parked item settled after the job
/// had already placed its files) inserted a second row for a path the series already had.
/// <c>CbzLinkService</c> now looks the row up by <see cref="LibraryPaths.ComparisonKey"/> before
/// inserting; this pass folds the rows that were written before it did. Within a duplicate set the
/// lowest id is kept, every chapter pointing at another member is re-pointed at it, the reader's
/// archive cache forgets the removed ids, and the extra rows are deleted.
/// </para>
/// <para>
/// Marker-gated in AppConfig like <see cref="SeriesIdentityRepairService"/>, and run at startup
/// before Kestrel and Quartz so it cannot overlap a live import.
/// </para>
/// </summary>
public class ChapterFileDuplicateRepairService(
    MakiDbContext db,
    ReaderArchiveCache archives,
    ILogger<ChapterFileDuplicateRepairService> logger)
{
    public const string MarkerKey = "library.chapterFileDuplicateRepairDone";

    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        if (await db.AppConfig.AnyAsync(c => c.Key == MarkerKey, ct))
        {
            return;
        }

        var (groups, removed) = await MergeAsync(ct);

        db.AppConfig.Add(new AppConfigEntry
        {
            Key = MarkerKey,
            Value = DateTime.UtcNow.ToString("O")
        });
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Chapter file duplicate repair complete: merged {Groups} duplicated path(s), removed {Removed} extra row(s)",
            groups, removed);
    }

    /// <summary>Merges every duplicate set. Returns how many paths were duplicated and how many rows went.</summary>
    public async Task<(int Groups, int Removed)> MergeAsync(CancellationToken ct = default)
    {
        var rows = await db.ChapterFiles.AsNoTracking().IgnoreQueryFilters()
            .Select(f => new { f.Id, f.SeriesId, f.RelativePath })
            .ToListAsync(ct);

        // Case folds only where the filesystem does: on Linux "Ch 1.cbz" and "ch 1.cbz" are two files.
        var duplicates = rows
            .GroupBy(f => (f.SeriesId, Key: OperatingSystem.IsWindows()
                ? LibraryPaths.ComparisonKey(f.RelativePath).ToUpperInvariant()
                : LibraryPaths.ComparisonKey(f.RelativePath)))
            .Where(g => g.Count() > 1)
            .Select(g => g.OrderBy(f => f.Id).Select(f => f.Id).ToList())
            .ToList();
        if (duplicates.Count == 0)
        {
            return (0, 0);
        }

        var removed = 0;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var ids in duplicates)
        {
            var keep = ids[0];
            var extras = ids.Skip(1).ToList();

            await db.Chapters.IgnoreQueryFilters()
                .Where(c => c.ChapterFileId != null && extras.Contains(c.ChapterFileId.Value))
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.ChapterFileId, keep), ct);

            // The health index re-derives this on its next scan, but pointing it at the survivor now
            // keeps the worker from reading the survivor as a file it has never analyzed.
            await db.HealthFiles
                .Where(h => h.ChapterFileId != null && extras.Contains(h.ChapterFileId.Value))
                .ExecuteUpdateAsync(u => u.SetProperty(h => h.ChapterFileId, keep), ct);

            // SQLite reuses rowids after a delete, so a later adopt can land on one of these ids
            // with a different archive behind it and the cache's size guard would not notice.
            foreach (var id in extras)
            {
                archives.Invalidate(id);
            }

            removed += await db.ChapterFiles.IgnoreQueryFilters()
                .Where(f => extras.Contains(f.Id))
                .ExecuteDeleteAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return (duplicates.Count, removed);
    }
}
