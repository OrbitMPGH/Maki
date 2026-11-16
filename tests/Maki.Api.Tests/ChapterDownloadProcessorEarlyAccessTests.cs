using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Sources;

namespace Maki.Api.Tests;

/// <summary>A chapter still behind early access waits for its release instead of failing.</summary>
[Collection(ConfigDirCollection.Name)]
public class ChapterDownloadProcessorEarlyAccessTests : IDisposable
{
    private readonly UpgradeWorld _world = new();
    private readonly int _chapterId;
    private readonly DateTimeOffset _unlockAt = DateTimeOffset.UtcNow.AddDays(2);

    public ChapterDownloadProcessorEarlyAccessTests()
    {
        _world.Seed();
        Directory.CreateDirectory(_world.Library);
        using (var db = _world.Db.NewContext())
        {
            db.SourceMappings.Single(m => m.Id == _world.OfficialMappingId).Priority = 1;
            db.SourceMappings.Single(m => m.Id == _world.AggMappingId).Enabled = false;
            db.SaveChanges();
        }

        (_chapterId, _) = _world.Chapter(1, withFile: false);
        _world.OfficialListing = id => [new SourceChapter(UpgradeWorld.Official, id, "o1", "1", 1m, null, null, "en", null)];
        _world.OfficialPages = _ => throw new ChapterLockedException("Early access", _unlockAt);
    }

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task A_locked_chapter_waits_until_its_unlock_without_spending_an_attempt_or_notifying()
    {
        var id = Enqueue();

        await _world.ProcessAsync(id);

        using var check = _world.Db.NewContext();
        var row = check.DownloadQueue.Single(q => q.Id == id);
        Assert.Equal(QueueStatus.Waiting, row.Status);
        Assert.Equal(ChapterDownloadProcessor.EarlyAccessKey, row.ErrorKey);
        Assert.Equal(0, row.RetryCount);
        Assert.True(row.NextAttempt > _unlockAt.UtcDateTime, $"next attempt {row.NextAttempt} is before the unlock");
        Assert.Empty(_world.Notifications.Sent);
        Assert.Equal(0, await _world.Queue.RequeueEligibleFailuresAsync(5));
    }

    private int Enqueue()
    {
        using var db = _world.Db.NewContext();
        var item = new DownloadQueueItem
        {
            SeriesId = _world.SeriesId, ChapterId = _chapterId, Protocol = AcquisitionProtocol.Scraper,
            Status = QueueStatus.Queued, QueuedAt = DateTime.UtcNow, Origin = DownloadOrigin.MonitorRefresh
        };
        db.DownloadQueue.Add(item);
        db.SaveChanges();
        return item.Id;
    }
}
