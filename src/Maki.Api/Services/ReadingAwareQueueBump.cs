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
/// Near the end means at most <see cref="DownloadsAhead"/> downloaded chapters sit between the one
/// being read and the series' first queued chapter. Only the next <see cref="BumpCount"/> scraper rows of that series move, in
/// reading order, ahead of everything claimable and without renumbering anyone else. Only the
/// chapter's own language counts, as for the reader's next chapter. A series is looked at again when
/// the reader moves to another chapter, else once per <see cref="Window"/>, and nothing is written
/// when its rows already lead.
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

    /// <summary>The shortest time between two looks at one series while the reader stays in one chapter.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<int, (int ChapterId, DateTime At)> _lastLook = new();

    /// <summary>
    /// Fire and forget. The throttle is checked synchronously so a burst of page writes costs a
    /// dictionary lookup, and the work runs on its own scope and thread-pool thread.
    /// </summary>
    public void Notify(int seriesId, int chapterId)
    {
        if (!Due(seriesId, chapterId))
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

    /// <summary>
    /// A look that moved nothing still counts for the window, but a new chapter is a new answer:
    /// reading quickly through the last chapters on disk must not wait out a look made further back.
    /// </summary>
    internal bool Due(int seriesId, int chapterId)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var due = false;
        _lastLook.AddOrUpdate(
            seriesId,
            _ => { due = true; return (chapterId, now); },
            (_, last) =>
            {
                due = last.ChapterId != chapterId || now - last.At >= Window;
                return due ? (chapterId, now) : last;
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
            .Select(c => new { c.Id, c.Number, c.Volume, c.Language, HasFile = c.ChapterFileId != null })
            .ToListAsync(ct);

        var reading = chapters.FirstOrDefault(c => c.Id == chapterId);
        if (reading?.Number is not { } place || !reading.HasFile)
        {
            return 0;
        }

        // Another language's copies are other chapters to the reader (ReaderService.NeighboursAsync).
        chapters = chapters.Where(c => c.Language == reading.Language).ToList();

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
        var nextRows = ChapterOrder.Sort(
                candidates.Where(q => byId.TryGetValue(q.ChapterId, out var c) && !c.HasFile && c.Number > place),
                q => byId[q.ChapterId].Number, q => byId[q.ChapterId].Volume, q => q.Id)
            .Take(BumpCount)
            .ToList();
        if (nextRows.Count == 0)
        {
            return 0;
        }

        // Only what the reader can get through before the first queued gap counts as ahead, and
        // distinct numbers, so two copies of one chapter count once.
        var gap = byId[nextRows[0].ChapterId].Number!.Value;
        var ahead = chapters
            .Where(c => c.HasFile && c.Number is { } n && n > place && n < gap)
            .Select(c => c.Number!.Value)
            .Distinct()
            .Count();
        if (ahead > DownloadsAhead)
        {
            return 0;
        }

        var next = nextRows.Select(q => q.Id).ToList();

        var front = await db.DownloadQueue
            .Where(q => q.Protocol == AcquisitionProtocol.Scraper &&
                        (q.Status == QueueStatus.Queued || q.Status == QueueStatus.RateLimited))
            .OrderBy(q => q.Priority)
            .ThenBy(q => q.SortOrder)
            .ThenBy(q => q.QueuedAt)
            .Select(q => new { q.Id, q.SortOrder })
            .Take(next.Count)
            .ToListAsync(ct);
        if (front.Select(q => q.Id).SequenceEqual(next))
        {
            return 0;
        }

        // Below the lowest SortOrder in the queue, so nobody else is renumbered, and into the High
        // class, so a backlog row the reader is about to reach passes new releases too. A row a worker
        // claimed since the read no longer matches the status filter and keeps its place.
        var floor = await db.DownloadQueue
            .Where(q => q.Protocol == AcquisitionProtocol.Scraper &&
                        (q.Status == QueueStatus.Queued || q.Status == QueueStatus.RateLimited))
            .MinAsync(q => (int?)q.SortOrder, ct) ?? 0;
        var moved = 0;
        for (var i = 0; i < next.Count; i++)
        {
            var id = next[i];
            var order = floor - next.Count + i;
            moved += await db.DownloadQueue
                .Where(q => q.Id == id &&
                            (q.Status == QueueStatus.Queued || q.Status == QueueStatus.RateLimited))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(q => q.SortOrder, order)
                    .SetProperty(q => q.Priority, QueuePriority.High), ct);
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
