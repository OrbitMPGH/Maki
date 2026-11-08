using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// A retry somebody asks for, of a row that failed on a source that is no longer the first choice,
/// resolves again instead of replaying the source that failed.
/// </summary>
[Collection(ConfigDirCollection.Name)]
public class RetrySourceChoiceTests : IDisposable
{
    private readonly UpgradeWorld _world = new();
    private readonly int _chapterId;

    public RetrySourceChoiceTests()
    {
        _world.Seed();
        using (var db = _world.Db.NewContext())
        {
            db.SourceMappings.Single(m => m.Id == _world.OfficialMappingId).Priority = 1;
            db.SourceMappings.Single(m => m.Id == _world.AggMappingId).Priority = 2;
            db.SaveChanges();
        }

        (_chapterId, _) = _world.Chapter(1, withFile: false);
        _world.OfficialListing = id => [new SourceChapter(UpgradeWorld.Official, id, "o1", "1", 1m, null, null, "en", null)];
    }

    public void Dispose() => _world.Dispose();

    private int SeedFailed(int mappingId, string errorKey, Action<DownloadQueueItem>? configure = null)
    {
        using var db = _world.Db.NewContext();
        var item = new DownloadQueueItem
        {
            SeriesId = _world.SeriesId, ChapterId = _chapterId, Protocol = AcquisitionProtocol.Scraper,
            Status = QueueStatus.Failed, QueuedAt = DateTime.UtcNow.AddHours(-1), SourceMappingId = mappingId,
            SourceChapterId = "a1", ErrorKey = errorKey, RetryCount = 1, NextAttempt = DateTime.UtcNow.AddHours(-1),
        };
        configure?.Invoke(item);
        db.DownloadQueue.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    private DownloadQueueItem Row(int id)
    {
        using var db = _world.Db.NewContext();
        return db.DownloadQueue.Single(q => q.Id == id);
    }

    [Theory]
    [InlineData(DownloadFailureReason.ChallengeNotSolved)]
    [InlineData(DownloadFailureReason.SourceError)]
    public async Task Bulk_retry_drops_a_source_that_is_no_longer_the_first_choice(string errorKey)
    {
        var id = SeedFailed(_world.AggMappingId, errorKey);

        Assert.Equal(1, await _world.Queue.RetryFailedAsync(null, null, new DataScope()));

        var row = Row(id);
        Assert.Equal(QueueStatus.Queued, row.Status);
        Assert.Null(row.SourceMappingId);
        Assert.Null(row.SourceChapterId);
    }

    [Fact]
    public async Task Bulk_retry_keeps_a_source_that_is_still_the_first_choice()
    {
        var id = SeedFailed(_world.OfficialMappingId, DownloadFailureReason.SourceError);

        await _world.Queue.RetryFailedAsync(null, null, new DataScope());

        Assert.Equal(_world.OfficialMappingId, Row(id).SourceMappingId);
    }

    [Fact]
    public async Task The_automatic_retry_keeps_its_source()
    {
        var id = SeedFailed(_world.AggMappingId, DownloadFailureReason.SourceError);

        Assert.Equal(1, await _world.Queue.RequeueEligibleFailuresAsync(5));

        Assert.Equal(_world.AggMappingId, Row(id).SourceMappingId);
    }

    public static TheoryData<string> KeepsItsSource => ["pinned", "upgrade", "purchase", "disk"];

    [Theory]
    [MemberData(nameof(KeepsItsSource))]
    public async Task A_pin_an_upgrade_a_paywall_or_a_disk_failure_keeps_its_source(string kind)
    {
        var id = SeedFailed(_world.AggMappingId,
            kind switch
            {
                "purchase" => DownloadFailureReason.NeedsPurchase,
                "disk" => DownloadFailureReason.Disk,
                _ => DownloadFailureReason.SourceError,
            },
            item =>
            {
                if (kind == "pinned") item.PreferredMappingId = _world.AggMappingId;
                if (kind == "upgrade") item.UpgradeInfoJson = "{}";
            });

        await _world.Queue.RetryFailedAsync(null, null, new DataScope());

        Assert.Equal(_world.AggMappingId, Row(id).SourceMappingId);
    }

    [Fact]
    public async Task A_chapter_only_the_fallback_lists_keeps_the_fallback()
    {
        using (var seed = _world.Db.NewContext())
        {
            seed.ChapterSourceLinks.RemoveRange(seed.ChapterSourceLinks
                .Where(l => l.ChapterId == _chapterId && l.SourceMappingId == _world.OfficialMappingId));
            seed.SaveChanges();
            Assert.Contains(seed.ChapterSourceLinks, l => l.ChapterId == _chapterId && l.SourceMappingId == _world.AggMappingId);
        }

        var id = SeedFailed(_world.AggMappingId, DownloadFailureReason.SourceError);

        await _world.Queue.RetryFailedAsync(null, null, new DataScope());

        Assert.Equal(_world.AggMappingId, Row(id).SourceMappingId);
    }

    [Fact]
    public async Task A_row_taken_while_the_retry_looked_is_left_alone()
    {
        var id = SeedFailed(_world.AggMappingId, DownloadFailureReason.SourceError);
        using var db = _world.Db.NewContext();
        var stale = db.DownloadQueue.Single(q => q.Id == id);
        using (var other = _world.Db.NewContext())
        {
            other.DownloadQueue.Single(q => q.Id == id).Status = QueueStatus.Downloading;
            other.SaveChanges();
        }

        var gone = await _world.Queue.ReleaseOutrankedSourcesAsync(db, [stale], CancellationToken.None);

        Assert.Equal([id], gone);
        Assert.Equal(_world.AggMappingId, Row(id).SourceMappingId);
    }

    [Fact]
    public async Task Row_retry_resolves_again_onto_the_first_choice()
    {
        var id = SeedFailed(_world.AggMappingId, DownloadFailureReason.ChallengeNotSolved);
        using var batches = _world.Batches();
        using var db = _world.Db.NewContext();
        var controller = new QueueController(
            new TestLocalizer(), db, _world.Queue, batches, null!, null!, null!, null!, null!,
            NullLogger<QueueController>.Instance);

        Assert.IsType<NoContentResult>(await controller.Retry(id, CancellationToken.None));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        DownloadQueueItem row;
        while ((row = Row(id)).Status == QueueStatus.Resolving && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(QueueStatus.Queued, row.Status);
        Assert.Equal(_world.OfficialMappingId, row.SourceMappingId);
        Assert.Equal("o1", row.SourceChapterId);
    }
}
