using Microsoft.Extensions.Logging.Abstractions;
using Maki.Api.Services;
using Maki.Core.Entities;

namespace Maki.Api.Tests;

/// <summary>
/// Enqueue behaviour of <see cref="DownloadQueueService"/> (the cooldown maths live in
/// <see cref="DownloadQueueCooldownTests"/>): mapping selection, dedup, and channel signalling.
/// </summary>
public class DownloadQueueServiceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly TestDb _db = new();
    private readonly StoppedClock _clock = new(T0);
    private readonly DownloadQueueService _queue;

    public DownloadQueueServiceTests() => _queue = new DownloadQueueService(
        _db.ScopeFactory(), _clock, Sources.SingleChapterResolver(null, "fake", "low", "high"), NullLogger<DownloadQueueService>.Instance);

    public void Dispose() => _db.Dispose();

    /// <summary>Seeds a series with the given mappings plus one chapter; returns (seriesId, chapterId).</summary>
    private (int SeriesId, int ChapterId) SeedChapter(params SourceMapping[] mappings)
    {
        var seriesId = _db.SeedSeries(mappings: mappings);
        using var db = _db.NewContext();
        var chapter = new Chapter { SeriesId = seriesId, Number = 1m, Language = "en" };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return (seriesId, chapter.Id);
    }

    private static SourceMapping Mapping(string source, int priority = 1, bool enabled = true) => new()
    {
        SourceName = source,
        SourceSeriesId = "s",
        Url = $"https://{source}.test",
        Priority = priority,
        Enabled = enabled
    };

    /// <summary>
    /// Inserts a Resolving row directly — the state EnqueueChapterAsync leaves the item in before its
    /// detached background resolve runs — so ResolveAndActivateAsync's own logic can be driven and
    /// awaited deterministically instead of racing the fire-and-forget call EnqueueChapterAsync makes.
    /// </summary>
    private DownloadQueueItem SeedResolvingItem(int seriesId, int chapterId)
    {
        using var db = _db.NewContext();
        var item = new DownloadQueueItem
        {
            SeriesId = seriesId,
            ChapterId = chapterId,
            Protocol = AcquisitionProtocol.Scraper,
            Status = QueueStatus.Resolving,
            QueuedAt = T0.UtcDateTime
        };
        db.DownloadQueue.Add(item);
        db.SaveChanges();
        return item;
    }

    [Fact]
    public async Task Enqueue_creates_a_resolving_item_and_does_not_signal_the_channel_yet()
    {
        var (_, chapterId) = SeedChapter(Mapping("fake"));

        var item = await _queue.EnqueueChapterAsync(chapterId);

        Assert.NotNull(item);
        Assert.Equal(QueueStatus.Resolving, item!.Status);
        Assert.Null(item.SourceMappingId);
        Assert.Equal(AcquisitionProtocol.Scraper, item.Protocol);
        Assert.Equal(T0.UtcDateTime, item.QueuedAt);
    }

    [Fact]
    public async Task Resolve_flips_a_resolving_item_to_queued_and_signals_the_channel()
    {
        var (seriesId, chapterId) = SeedChapter(Mapping("fake"));
        var item = SeedResolvingItem(seriesId, chapterId);

        await _queue.ResolveAndActivateAsync(item.Id, chapterId, CancellationToken.None);

        using var db = _db.NewContext();
        var updated = db.DownloadQueue.Single(q => q.Id == item.Id);
        Assert.Equal(QueueStatus.Queued, updated.Status);
        Assert.NotNull(updated.SourceMappingId);
        Assert.True(_queue.Reader.TryRead(out var signalledId));
        Assert.Equal(item.Id, signalledId);
    }

    [Fact]
    public async Task Resolve_picks_the_lowest_priority_value_enabled_mapping()
    {
        var (seriesId, chapterId) = SeedChapter(Mapping("low", priority: 5), Mapping("high", priority: 1));
        var item = SeedResolvingItem(seriesId, chapterId);

        await _queue.ResolveAndActivateAsync(item.Id, chapterId, CancellationToken.None);

        using var db = _db.NewContext();
        var updated = db.DownloadQueue.Single(q => q.Id == item.Id);
        var chosen = db.SourceMappings.Single(m => m.Id == updated.SourceMappingId);
        Assert.Equal("high", chosen.SourceName);
    }

    [Fact]
    public async Task Enqueue_is_idempotent_while_an_item_is_active()
    {
        var (_, chapterId) = SeedChapter(Mapping("fake"));

        var first = await _queue.EnqueueChapterAsync(chapterId);
        var second = await _queue.EnqueueChapterAsync(chapterId);

        Assert.NotNull(first);
        Assert.Null(second);
        using var db = _db.NewContext();
        Assert.Equal(1, db.DownloadQueue.Count(q => q.ChapterId == chapterId));
    }

    [Fact]
    public async Task A_finished_prior_attempt_does_not_block_re_enqueue()
    {
        var (seriesId, chapterId) = SeedChapter(Mapping("fake"));
        using (var db = _db.NewContext())
        {
            db.DownloadQueue.Add(new DownloadQueueItem
            {
                SeriesId = seriesId, ChapterId = chapterId, Status = QueueStatus.Completed, QueuedAt = T0.UtcDateTime
            });
            db.SaveChanges();
        }

        var item = await _queue.EnqueueChapterAsync(chapterId);

        Assert.NotNull(item);
        using var check = _db.NewContext();
        Assert.Equal(2, check.DownloadQueue.Count(q => q.ChapterId == chapterId));
    }

    [Fact]
    public async Task Enqueuing_a_missing_chapter_throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _queue.EnqueueChapterAsync(999));
    }

    [Fact]
    public async Task Enqueuing_with_no_enabled_mapping_throws()
    {
        var (_, chapterId) = SeedChapter(Mapping("fake", enabled: false));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _queue.EnqueueChapterAsync(chapterId));
    }

    [Fact]
    public async Task Globally_disabled_source_is_skipped_in_favour_of_the_next_mapping()
    {
        var (seriesId, chapterId) = SeedChapter(Mapping("off", priority: 1), Mapping("on", priority: 2));
        var item = SeedResolvingItem(seriesId, chapterId);
        var queue = new DownloadQueueService(
            _db.ScopeFactory(), _clock, Sources.SingleChapterResolver(Sources.Disabled("off"), "off", "on"), NullLogger<DownloadQueueService>.Instance);

        await queue.ResolveAndActivateAsync(item.Id, chapterId, CancellationToken.None);

        using var check = _db.NewContext();
        var updated = check.DownloadQueue.Single(q => q.Id == item.Id);
        Assert.Equal("on", check.SourceMappings.Single(m => m.Id == updated.SourceMappingId).SourceName);
    }

    [Fact]
    public async Task Enqueuing_throws_when_every_mapping_is_globally_disabled()
    {
        var (_, chapterId) = SeedChapter(Mapping("off"));
        var queue = new DownloadQueueService(
            _db.ScopeFactory(), _clock, Sources.SingleChapterResolver(Sources.Disabled("off"), "off", "on"), NullLogger<DownloadQueueService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueChapterAsync(chapterId));
    }
}
