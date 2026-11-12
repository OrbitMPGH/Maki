using System.Collections.Concurrent;
using Maki.Api.Hubs;
using Maki.Core.Entities;
using Maki.Core.Reading;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Moves a series' next queued chapters to the front of the download queue when a reader nears the
/// end of what is on disk. Called from the progress write path after the write has committed, so
/// every reader client benefits; it never delays that write and never throws into it.
/// <para>
/// Near the end means the chapter being read has at most <see cref="DownloadsAhead"/> downloaded
/// chapters after it. Only the next <see cref="BumpCount"/> scraper rows of that series move, in
/// reading order, ahead of everything claimable and without renumbering anyone else. A series is
/// looked at once per <see cref="Window"/>, and nothing is written when its rows already lead.
/// </para>
/// <para>
/// Rows in flight, <see cref="QueueStatus.Waiting"/> (an early-access chapter nothing can fetch
/// yet), Failed, parked torrents and rows for chapters that already have a file are left alone.
/// The queue has no pin of its own: a manual drag only writes the same <c>SortOrder</c>, so it
/// cannot be told apart from an earlier bump.
/// </para>
/// </summary>
public class ReadingAwareQueueBump(
    IServiceScopeFactory scopeFactory, EventBroadcaster events, TimeProvider time,
    ILogger<ReadingAwareQueueBump> logger)
{
    /// <summary>Downloaded chapters left after the one being read at which the bump starts.</summary>
    public const int DownloadsAhead = 1;

    /// <summary>How many of the series' next chapters move to the front.</summary>
    public const int BumpCount = 3;

    /// <summary>The shortest time between two looks at one series.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<int, DateTime> _lastLook = new();

    /// <summary>
    /// Fire and forget. The throttle is checked synchronously so a burst of page writes costs a
    /// dictionary lookup, and the work runs on its own scope and thread-pool thread.
    /// </summary>
    public void Notify(int seriesId, int chapterId)
    {
        if (!Due(seriesId))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await BumpAsync(seriesId, chapterId, CancellationToken.None);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "Reading-aware queue bump for series {SeriesId} failed", seriesId);
            }
        });
    }

    internal bool Due(int seriesId)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var due = false;
        _lastLook.AddOrUpdate(
            seriesId,
            _ => { due = true; return now; },
            (_, last) =>
            {
                due = now - last >= Window;
                return due ? now : last;
            });
        return due;
    }

    /// <summary>Does the work of <see cref="Notify"/> without the throttle. Returns the rows moved.</summary>
    public async Task<int> BumpAsync(int seriesId, int chapterId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        var chapters = await db.Chapters
            .AsNoTracking()
            .Where(c => c.SeriesId == seriesId)
            .Select(c => new { c.Id, c.Number, c.Volume, HasFile = c.ChapterFileId != null })
            .ToListAsync(ct);

        var reading = chapters.FirstOrDefault(c => c.Id == chapterId);
        if (reading?.Number is not { } place || !reading.HasFile)
        {
            return 0;
        }

        // Distinct numbers, so a second language's copy of a chapter is not another one to read.
        var ahead = chapters
            .Where(c => c.HasFile && c.Number is { } n && n > place)
            .Select(c => c.Number!.Value)
            .Distinct()
            .Count();
        if (ahead > DownloadsAhead)
        {
            return 0;
        }

        var candidates = await db.DownloadQueue
            .Where(q => q.SeriesId == seriesId && q.Protocol == AcquisitionProtocol.Scraper &&
                        q.ChapterId != null &&
                        (q.Status == QueueStatus.Queued || q.Status == QueueStatus.RateLimited))
            .Select(q => new { q.Id, ChapterId = q.ChapterId!.Value })
            .ToListAsync(ct);
        if (candidates.Count == 0)
        {
            return 0;
        }

        var byId = chapters.ToDictionary(c => c.Id);
        var next = ChapterOrder.Sort(
                candidates.Where(q => byId.TryGetValue(q.ChapterId, out var c) && !c.HasFile && c.Number > place),
                q => byId[q.ChapterId].Number, q => byId[q.ChapterId].Volume, q => q.Id)
            .Take(BumpCount)
            .Select(q => q.Id)
            .ToList();
        if (next.Count == 0)
        {
            return 0;
        }

        var front = await db.DownloadQueue
            .Where(q => q.Protocol == AcquisitionProtocol.Scraper &&
                        (q.Status == QueueStatus.Queued || q.Status == QueueStatus.RateLimited))
            .OrderBy(q => q.SortOrder)
            .ThenBy(q => q.QueuedAt)
            .Select(q => new { q.Id, q.SortOrder })
            .Take(next.Count)
            .ToListAsync(ct);
        if (front.Select(q => q.Id).SequenceEqual(next))
        {
            return 0;
        }

        // Below the lowest SortOrder in the queue, so nobody else is renumbered. A row a worker
        // claimed since the read no longer matches the status filter and keeps its place.
        var floor = front.Count == 0 ? 0 : front[0].SortOrder;
        var moved = 0;
        for (var i = 0; i < next.Count; i++)
        {
            var id = next[i];
            var order = floor - next.Count + i;
            moved += await db.DownloadQueue
                .Where(q => q.Id == id &&
                            (q.Status == QueueStatus.Queued || q.Status == QueueStatus.RateLimited))
                .ExecuteUpdateAsync(s => s.SetProperty(q => q.SortOrder, order), ct);
        }

        if (moved > 0)
        {
            logger.LogDebug("Moved {Count} queued chapters of series {SeriesId} to the front for a reader nearing the end",
                moved, seriesId);
            await events.QueueReordered(seriesId);
        }

        return moved;
    }
}
