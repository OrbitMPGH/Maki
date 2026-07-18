using Mangarr.Api.Controllers;
using Mangarr.Api.Dtos;
using Mangarr.Api.Services;
using Mangarr.Core.Entities;
using Microsoft.AspNetCore.Mvc;

namespace Mangarr.Api.Tests;

/// <summary>State transitions in <see cref="QueueController"/>: list filtering, retry, and removal.</summary>
public class QueueControllerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly DownloadQueueService _queue;
    private readonly int _seriesId;

    public QueueControllerTests()
    {
        _queue = new DownloadQueueService(_db.ScopeFactory(), TimeProvider.System);
        _seriesId = _db.SeedSeries();
    }

    public void Dispose() => _db.Dispose();

    private QueueController Controller() => new(_db.NewContext(), _queue);

    private int SeedItem(QueueStatus status)
    {
        using var db = _db.NewContext();
        var item = new DownloadQueueItem
        {
            SeriesId = _seriesId, Status = status, Protocol = AcquisitionProtocol.Torrent,
            Title = "release", QueuedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };
        db.DownloadQueue.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    private QueueStatus StatusOf(int id)
    {
        using var db = _db.NewContext();
        return db.DownloadQueue.Single(q => q.Id == id).Status;
    }

    [Fact]
    public async Task List_excludes_completed_and_cancelled()
    {
        SeedItem(QueueStatus.Queued);
        SeedItem(QueueStatus.Completed);
        SeedItem(QueueStatus.Cancelled);

        var result = await Controller().List(ct: CancellationToken.None);

        var dto = Assert.IsType<QueueHistoryDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(1, dto.Total);
    }

    [Fact]
    public async Task History_includes_only_completed_and_cancelled()
    {
        SeedItem(QueueStatus.Queued);
        SeedItem(QueueStatus.Completed);
        SeedItem(QueueStatus.Cancelled);

        var result = await Controller().History(ct: CancellationToken.None);

        var dto = Assert.IsType<QueueHistoryDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(2, dto.Total);
    }

    [Fact]
    public async Task Retry_missing_returns_not_found()
    {
        Assert.IsType<NotFoundResult>(await Controller().Retry(999, CancellationToken.None));
    }

    [Fact]
    public async Task Retry_rejects_a_non_failed_item()
    {
        var id = SeedItem(QueueStatus.Downloading);
        Assert.IsType<ConflictObjectResult>(await Controller().Retry(id, CancellationToken.None));
    }

    [Fact]
    public async Task Retry_requeues_a_failed_item_and_signals()
    {
        var id = SeedItem(QueueStatus.Failed);

        var result = await Controller().Retry(id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(QueueStatus.Queued, StatusOf(id));
        Assert.True(_queue.Reader.TryRead(out var signalled));
        Assert.Equal(id, signalled);
    }

    [Fact]
    public async Task Remove_deletes_a_queued_item()
    {
        var id = SeedItem(QueueStatus.Queued);

        var result = await Controller().Remove(id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        using var db = _db.NewContext();
        Assert.False(db.DownloadQueue.Any(q => q.Id == id));
    }

    [Fact]
    public async Task Remove_cancels_an_in_flight_item()
    {
        var id = SeedItem(QueueStatus.Downloading);

        var result = await Controller().Remove(id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(QueueStatus.Cancelled, StatusOf(id));
    }
}
