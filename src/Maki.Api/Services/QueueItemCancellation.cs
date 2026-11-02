using System.Linq.Expressions;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Takes queue rows out of play when the thing they download for goes away (a removed source
/// mapping, an archived series). Shared so both agree on what happens to each status.
/// </summary>
internal static class QueueItemCancellation
{
    /// <summary>
    /// Stops the local work on every unsettled row <paramref name="scope"/> matches and drops it from
    /// its download batch, so an open batch still sends its summary. Rows nothing has claimed yet are
    /// removed; the rest are marked Cancelled and stay in history. Changes are tracked, not saved.
    /// </summary>
    /// <returns>How many rows were removed or cancelled.</returns>
    public static async Task<int> CancelAsync(
        MakiDbContext db,
        DownloadQueueService queue,
        DownloadBatchNotifier batches,
        Expression<Func<DownloadQueueItem, bool>> scope,
        CancellationToken ct)
    {
        var active = await db.DownloadQueue
            .Where(q => q.Status != QueueStatus.Completed && q.Status != QueueStatus.Cancelled)
            .Where(scope)
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

        return active.Count;
    }
}
