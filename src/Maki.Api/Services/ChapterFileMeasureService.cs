using Maki.Core.Paths;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Measures every <c>ChapterFile</c> that has never been measured: rows written before the quality
/// columns existed, and imports, adoptions and relinks, which stamp a tier but never open the
/// archive. Runs from <c>ChapterFileMeasureJob</c>. Measuring only reads the archive, so the
/// reader's cached page lists stay valid and nothing is invalidated.
/// </summary>
public class ChapterFileMeasureService(
    MakiDbContext db, ChapterFileQualityService quality, ILogger<ChapterFileMeasureService> logger)
{
    public const int BatchSize = 50;
    public const int SampleSize = 12;

    private static readonly SemaphoreSlim Gate = new(1);

    /// <summary>Pause between files so a large library does not keep the disk busy for the whole pass.</summary>
    internal TimeSpan DelayBetweenFiles { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <returns>false when a pass is already running, so this call did nothing.</returns>
    public async Task<bool> RunAsync(CancellationToken ct)
    {
        if (!await Gate.WaitAsync(0, ct))
        {
            return false;
        }

        var measured = 0;
        var skipped = 0;
        try
        {
            // A cursor rather than re-querying from the start: a file that is not on disk stays
            // unmeasured, and would otherwise come back at the head of every batch.
            var lastId = 0;
            while (true)
            {
                var batch = await db.ChapterFiles.IgnoreQueryFilters().AsNoTracking()
                    .Where(f => f.MeasuredAtUtc == null && f.Id > lastId)
                    .OrderBy(f => f.Id)
                    .Take(BatchSize)
                    .ToListAsync(ct);
                if (batch.Count == 0)
                {
                    break;
                }

                lastId = batch[^1].Id;

                var seriesIds = batch.Select(f => f.SeriesId).Distinct().ToList();
                var roots = await db.Series.IgnoreQueryFilters()
                    .Where(s => seriesIds.Contains(s.Id) && s.RootFolder != null)
                    .Select(s => new { s.Id, s.RootFolder!.Path })
                    .ToDictionaryAsync(s => s.Id, s => s.Path, ct);

                var fileIds = batch.Select(f => f.Id).ToList();
                var chapterByFile = (await db.Chapters.IgnoreQueryFilters().AsNoTracking()
                        .Where(c => c.ChapterFileId != null && fileIds.Contains(c.ChapterFileId.Value))
                        .Include(c => c.SourceLinks).ThenInclude(l => l.SourceMapping)
                        .ToListAsync(ct))
                    .GroupBy(c => c.ChapterFileId!.Value)
                    .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Id).First());

                foreach (var file in batch)
                {
                    ct.ThrowIfCancellationRequested();

                    var path = roots.TryGetValue(file.SeriesId, out var root)
                        ? LibraryPaths.Resolve(root, file.RelativePath)
                        : null;
                    if (path is null)
                    {
                        skipped++;
                        continue;
                    }

                    var (kind, group) = quality.ResolveProvenance(file, chapterByFile.GetValueOrDefault(file.Id));
                    quality.Stamp(file, path, kind, group, SampleSize, ct);

                    // Conditional on the row still being the one that was read: it may have been
                    // deleted, re-pointed at another path, or measured by a download since. None is
                    // an error; the write just matches nothing. Its own token, so a file measured
                    // before a cancellation is kept.
                    await db.ChapterFiles.IgnoreQueryFilters()
                        .Where(f => f.Id == file.Id && f.RelativePath == file.RelativePath && f.MeasuredAtUtc == null)
                        .ExecuteUpdateAsync(set => set
                            .SetProperty(f => f.Tier, file.Tier)
                            .SetProperty(f => f.Group, file.Group)
                            .SetProperty(f => f.PageCount, file.PageCount)
                            .SetProperty(f => f.MedianWidth, file.MedianWidth)
                            .SetProperty(f => f.MedianHeight, file.MedianHeight)
                            .SetProperty(f => f.ImageFormat, file.ImageFormat)
                            .SetProperty(f => f.MeasuredAtUtc, file.MeasuredAtUtc), CancellationToken.None);

                    if (file.MeasuredAtUtc is null)
                    {
                        skipped++;
                        continue;
                    }

                    measured++;
                    if (DelayBetweenFiles > TimeSpan.Zero)
                    {
                        await Task.Delay(DelayBetweenFiles, ct);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogInformation("Chapter file measurement cancelled after {Measured} file(s)", measured);
            return true;
        }
        finally
        {
            Gate.Release();
        }

        if (measured > 0 || skipped > 0)
        {
            logger.LogInformation(
                "Measured {Measured} chapter file(s); {Skipped} not readable were left for a later pass",
                measured, skipped);
        }

        return true;
    }

    /// <summary>
    /// Unmeasured rows under a root folder that is present. Counted in SQL without touching the
    /// files, so a file missing from a present root still counts; a missing root (an unmounted
    /// share) contributes nothing.
    /// </summary>
    public static async Task<int> CountPendingAsync(MakiDbContext db, CancellationToken ct)
    {
        var byRoot = await db.ChapterFiles.IgnoreQueryFilters()
            .Where(f => f.MeasuredAtUtc == null)
            .Join(db.Series.IgnoreQueryFilters().Where(s => s.RootFolder != null),
                f => f.SeriesId, s => s.Id, (f, s) => s.RootFolder!.Path)
            .GroupBy(path => path)
            .Select(g => new { Path = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return byRoot.Where(r => Directory.Exists(r.Path)).Sum(r => r.Count);
    }
}
