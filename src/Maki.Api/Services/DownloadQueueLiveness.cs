using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Answers which of a set of queue item ids are still active (not completed, failed or cancelled).
/// <see cref="DownloadBatchNotifier"/> asks before closing a batch that has gone quiet: an item
/// waiting its turn behind a long backlog reports nothing for hours and is not leaked.
/// </summary>
public interface IDownloadQueueLiveness
{
    Task<IReadOnlySet<int>> StillActiveAsync(IReadOnlyCollection<int> queueItemIds, CancellationToken ct = default);
}

public sealed class DownloadQueueLiveness(IServiceScopeFactory scopes) : IDownloadQueueLiveness
{
    public async Task<IReadOnlySet<int>> StillActiveAsync(IReadOnlyCollection<int> queueItemIds, CancellationToken ct = default)
    {
        if (queueItemIds.Count == 0)
        {
            return new HashSet<int>();
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        // ActiveChapterId is the computed "not in a terminal status" column that backs the one
        // active row per chapter index, so it is exactly the liveness test the queue itself uses.
        var ids = queueItemIds.ToList();
        var alive = await db.DownloadQueue.IgnoreQueryFilters()
            .Where(i => ids.Contains(i.Id) && i.ActiveChapterId != null)
            .Select(i => i.Id)
            .ToListAsync(ct);
        return alive.ToHashSet();
    }
}
