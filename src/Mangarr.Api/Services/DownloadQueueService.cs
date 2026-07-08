using System.Threading.Channels;
using Mangarr.Core.Entities;
using Mangarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Services;

/// <summary>
/// Persists download queue items and feeds their ids to the worker via a channel.
/// Singleton; DB access goes through short-lived scopes.
/// </summary>
public class DownloadQueueService(IServiceScopeFactory scopeFactory)
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>();

    public ChannelReader<int> Reader => _channel.Reader;

    /// <summary>Queues a chapter for download from its best (lowest-priority-value) enabled mapping.</summary>
    public async Task<DownloadQueueItem?> EnqueueChapterAsync(int chapterId, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangarrDbContext>();

        var chapter = await db.Chapters.FirstOrDefaultAsync(c => c.Id == chapterId, ct)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found");

        var alreadyQueued = await db.DownloadQueue.AnyAsync(q =>
            q.ChapterId == chapterId &&
            q.Status != QueueStatus.Completed &&
            q.Status != QueueStatus.Failed &&
            q.Status != QueueStatus.Cancelled, ct);
        if (alreadyQueued)
        {
            return null;
        }

        var mapping = await db.SourceMappings
            .Where(m => m.SeriesId == chapter.SeriesId && m.Enabled)
            .OrderBy(m => m.Priority)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Series has no enabled source mappings");

        var item = new DownloadQueueItem
        {
            ChapterId = chapterId,
            SourceMappingId = mapping.Id,
            Protocol = AcquisitionProtocol.Scraper,
            Status = QueueStatus.Queued,
            QueuedAt = DateTime.UtcNow
        };
        db.DownloadQueue.Add(item);
        await db.SaveChangesAsync(ct);

        await _channel.Writer.WriteAsync(item.Id, ct);
        return item;
    }

    /// <summary>Re-signals an existing queue item (startup recovery, manual retry).</summary>
    public ValueTask SignalAsync(int queueItemId, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(queueItemId, ct);
}
