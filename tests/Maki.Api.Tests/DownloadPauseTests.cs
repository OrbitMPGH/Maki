using Microsoft.Extensions.Logging.Abstractions;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;

namespace Maki.Api.Tests;

/// <summary>
/// The pause gate on <see cref="DownloadQueueService.ClaimNextAsync"/>: what it stops, what it leaves
/// alone, when it lifts and that it outlives the service holding it.
/// </summary>
public class DownloadPauseTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly TestDb _db = new();
    private readonly Clock _clock = new(T0);
    private readonly FakeAppSettings _settings = new();
    private readonly DownloadPauseService _pauses;
    private readonly DownloadQueueService _queue;

    public DownloadPauseTests()
    {
        _pauses = new DownloadPauseService(_settings, _clock);
        _queue = new DownloadQueueService(
            _db.ScopeFactory(), _clock, Sources.SingleChapterResolver(null, "fake"),
            NullLogger<DownloadQueueService>.Instance, _pauses);
    }

    public void Dispose() => _db.Dispose();

    private int SeedItem(string sourceName, QueueStatus status = QueueStatus.Queued, int sortOrder = 0)
    {
        var seriesId = _db.SeedSeries(mappings:
            [new SourceMapping { SourceName = sourceName, SourceSeriesId = "s", Url = $"https://{sourceName}.test", Priority = 1 }]);

        using var db = _db.NewContext();
        var chapter = new Chapter { SeriesId = seriesId, Number = 1m, Language = "en" };
        db.Chapters.Add(chapter);
        db.SaveChanges();

        var item = new DownloadQueueItem
        {
            SeriesId = seriesId,
            ChapterId = chapter.Id,
            Protocol = AcquisitionProtocol.Scraper,
            Status = status,
            SourceMappingId = db.SourceMappings.Single(m => m.SeriesId == seriesId).Id,
            SortOrder = sortOrder,
            QueuedAt = T0.UtcDateTime
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
    public async Task Nothing_is_paused_by_default()
    {
        var id = SeedItem("a");

        Assert.True((await _pauses.ActiveAsync()).IsEmpty);
        Assert.Equal(id, await _queue.ClaimNextAsync());
    }

    [Fact]
    public async Task A_global_pause_claims_nothing_and_changes_no_status()
    {
        var id = SeedItem("a");
        await _pauses.PauseAsync(null, null);

        Assert.Null(await _queue.ClaimNextAsync());
        Assert.Equal(QueueStatus.Queued, StatusOf(id));
    }

    [Fact]
    public async Task Resuming_makes_the_queue_claimable_again()
    {
        var id = SeedItem("a");
        await _pauses.PauseAsync(null, null);
        await _pauses.ResumeAsync(null);

        Assert.Equal(id, await _queue.ClaimNextAsync());
    }

    [Fact]
    public async Task A_source_pause_skips_that_source_and_still_claims_the_others()
    {
        SeedItem("paused", sortOrder: 0);
        var other = SeedItem("warm", sortOrder: 1);
        await _pauses.PauseAsync("paused", null);

        Assert.Equal(other, await _queue.ClaimNextAsync());
        Assert.Null(await _queue.ClaimNextAsync());
    }

    [Fact]
    public async Task A_source_pause_skips_rate_limited_rows_of_that_source_too()
    {
        SeedItem("paused", QueueStatus.RateLimited, sortOrder: 0);
        var other = SeedItem("warm", QueueStatus.RateLimited, sortOrder: 1);
        await _pauses.PauseAsync("paused", null);

        Assert.Equal(other, await _queue.ClaimNextAsync());
    }

    [Fact]
    public async Task Resuming_one_source_leaves_the_global_pause_alone()
    {
        SeedItem("a");
        await _pauses.PauseAsync(null, null);
        await _pauses.PauseAsync("a", null);
        await _pauses.ResumeAsync("a");

        var state = await _pauses.ActiveAsync();
        Assert.NotNull(state.All);
        Assert.Empty(state.Sources);
        Assert.Null(await _queue.ClaimNextAsync());
    }

    [Fact]
    public async Task An_item_already_in_flight_is_unaffected_by_a_pause()
    {
        var running = SeedItem("a", sortOrder: 0);
        var waiting = SeedItem("a", sortOrder: 1);
        Assert.Equal(running, await _queue.ClaimNextAsync());

        await _pauses.PauseAsync(null, null);

        Assert.Equal(QueueStatus.FetchingPages, StatusOf(running));
        Assert.Null(await _queue.ClaimNextAsync());
        Assert.Equal(QueueStatus.Queued, StatusOf(waiting));
        Assert.Equal(0, await _queue.SweepOrphanedAsync());
        Assert.Equal(QueueStatus.FetchingPages, StatusOf(running));
    }

    [Fact]
    public async Task A_pause_lifts_by_itself_at_its_resume_time()
    {
        var id = SeedItem("a");
        await _pauses.PauseAsync(null, T0.UtcDateTime.AddHours(1));

        _clock.Now = T0.AddMinutes(59);
        Assert.Null(await _queue.ClaimNextAsync());

        _clock.Now = T0.AddHours(1);
        Assert.True((await _pauses.ActiveAsync()).IsEmpty);
        Assert.Equal(id, await _queue.ClaimNextAsync());
    }

    [Fact]
    public async Task A_source_pause_lifts_by_itself_at_its_resume_time()
    {
        var id = SeedItem("a");
        await _pauses.PauseAsync("a", T0.UtcDateTime.AddMinutes(30));
        Assert.Null(await _queue.ClaimNextAsync());

        _clock.Now = T0.AddMinutes(31);

        Assert.Equal(id, await _queue.ClaimNextAsync());
    }

    [Fact]
    public async Task Expire_clears_a_lapsed_pause_once_and_reports_it()
    {
        await _pauses.PauseAsync("a", T0.UtcDateTime.AddMinutes(30));
        await _pauses.PauseAsync("b", null);
        Assert.False(await _pauses.ExpireAsync());

        _clock.Now = T0.AddMinutes(31);

        Assert.True(await _pauses.ExpireAsync());
        Assert.False(await _pauses.ExpireAsync());
        Assert.Equal(["b"], (await _pauses.ActiveAsync()).Sources.Keys);
    }

    [Fact]
    public async Task A_pause_survives_rebuilding_the_service()
    {
        var id = SeedItem("a");
        await _pauses.PauseAsync("a", T0.UtcDateTime.AddHours(2));
        await _pauses.PauseAsync(null, null);

        var rebuilt = new DownloadPauseService(_settings, _clock);
        var state = await rebuilt.ActiveAsync();

        Assert.NotNull(state.All);
        Assert.Equal(T0.UtcDateTime.AddHours(2), state.Sources["a"].Until);
        var queue = new DownloadQueueService(
            _db.ScopeFactory(), _clock, Sources.SingleChapterResolver(null, "fake"),
            NullLogger<DownloadQueueService>.Instance, rebuilt);
        Assert.Null(await queue.ClaimNextAsync());
        Assert.Equal(QueueStatus.Queued, StatusOf(id));
    }

    [Fact]
    public async Task Resuming_everything_removes_the_stored_setting()
    {
        await _pauses.PauseAsync(null, null);
        await _pauses.ResumeAsync(null);

        Assert.Null(await _settings.GetAsync(SettingKeys.DownloadPause));
    }

    [Fact]
    public async Task A_corrupt_stored_value_reads_as_nothing_paused()
    {
        var id = SeedItem("a");
        _settings.Set(SettingKeys.DownloadPause, "{not json");

        Assert.True((await _pauses.ActiveAsync()).IsEmpty);
        Assert.Equal(id, await _queue.ClaimNextAsync());
    }
}
