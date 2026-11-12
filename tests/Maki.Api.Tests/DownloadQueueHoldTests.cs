using Microsoft.Extensions.Logging.Abstractions;
using Maki.Api.Services;
using Maki.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Tests;

/// <summary>
/// What holds one series or one root folder back without a global or source pause: the series pause
/// and the low disk space hold, both on <see cref="DownloadQueueService.ClaimNextAsync"/>.
/// </summary>
public class DownloadQueueHoldTests : IDisposable
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
    private readonly Dictionary<string, long?> _free = new();
    private int _reads;
    private readonly DownloadDiskHold _disk;
    private readonly DownloadQueueService _queue;

    public DownloadQueueHoldTests()
    {
        _pauses = new DownloadPauseService(_settings, _clock);
        _disk = new DownloadDiskHold(_db.ScopeFactory(), _clock, NullLogger<DownloadDiskHold>.Instance, path =>
        {
            _reads++;
            return _free.GetValueOrDefault(path, long.MaxValue);
        });
        _queue = new DownloadQueueService(
            _db.ScopeFactory(), _clock, Sources.SingleChapterResolver(null, "fake"),
            NullLogger<DownloadQueueService>.Instance, _pauses, _disk);
    }

    public void Dispose() => _db.Dispose();

    private (int SeriesId, int ItemId, string Root) SeedItem(int sortOrder = 0, long? minFree = null)
    {
        var seriesId = _db.SeedSeries(mappings:
            [new SourceMapping { SourceName = "a", SourceSeriesId = "s", Url = "https://a.test", Priority = 1 }]);

        using var db = _db.NewContext();
        var series = db.Series.Include(s => s.RootFolder).Single(s => s.Id == seriesId);
        series.RootFolder!.Path = $"/manga{seriesId}";
        series.RootFolder.MinFreeBytes = minFree;
        var chapter = new Chapter { SeriesId = seriesId, Number = 1m, Language = "en" };
        db.Chapters.Add(chapter);
        db.SaveChanges();

        var item = new DownloadQueueItem
        {
            SeriesId = seriesId,
            ChapterId = chapter.Id,
            Protocol = AcquisitionProtocol.Scraper,
            Status = QueueStatus.Queued,
            SourceMappingId = db.SourceMappings.Single(m => m.SeriesId == seriesId).Id,
            SortOrder = sortOrder,
            QueuedAt = T0.UtcDateTime
        };
        db.DownloadQueue.Add(item);
        db.SaveChanges();
        return (seriesId, item.Id, series.RootFolder.Path);
    }

    [Fact]
    public async Task A_paused_series_is_skipped_and_the_rest_still_claims()
    {
        var paused = SeedItem(sortOrder: 0);
        var other = SeedItem(sortOrder: 1);
        await _pauses.PauseSeriesAsync(paused.SeriesId, null);

        Assert.Equal(other.ItemId, await _queue.ClaimNextAsync());
        Assert.Null(await _queue.ClaimNextAsync());

        await _pauses.ResumeSeriesAsync(paused.SeriesId);
        Assert.Equal(paused.ItemId, await _queue.ClaimNextAsync());
    }

    [Fact]
    public async Task A_series_pause_lifts_at_its_resume_time_and_survives_a_rebuild()
    {
        var row = SeedItem();
        await _pauses.PauseSeriesAsync(row.SeriesId, T0.UtcDateTime.AddHours(1));

        var rebuilt = new DownloadPauseService(_settings, _clock);
        Assert.True((await rebuilt.ActiveAsync()).PausedSeries.ContainsKey(row.SeriesId));
        Assert.Null(await _queue.ClaimNextAsync());

        _clock.Now = T0.AddHours(1);
        Assert.Equal(row.ItemId, await _queue.ClaimNextAsync());
    }

    [Fact]
    public async Task A_pause_stored_before_series_pauses_existed_still_reads()
    {
        _settings.Set(Maki.Core.Configuration.SettingKeys.DownloadPause, """{"all":null,"sources":{"a":{"until":null}}}""");

        var state = await _pauses.ActiveAsync();

        Assert.Empty(state.PausedSeries);
        Assert.True(state.Sources.ContainsKey("a"));
        await _pauses.PauseSeriesAsync(7, null);
        Assert.True((await _pauses.ActiveAsync()).Sources.ContainsKey("a"));
    }

    [Fact]
    public async Task Rows_into_a_root_below_its_threshold_are_held_and_others_are_not()
    {
        var low = SeedItem(sortOrder: 0);
        var fine = SeedItem(sortOrder: 1);
        _free[low.Root] = 2L * 1024 * 1024 * 1024;

        Assert.Equal(fine.ItemId, await _queue.ClaimNextAsync());
        Assert.Null(await _queue.ClaimNextAsync());
        var hold = Assert.Single(_disk.Current);
        Assert.Equal(low.Root, hold.Path);
        Assert.Equal(RootFolder.DefaultMinFreeBytes, hold.MinFreeBytes);
    }

    [Fact]
    public async Task The_hold_lifts_once_space_returns_after_the_reading_ages_out()
    {
        var low = SeedItem();
        _free[low.Root] = 1024;
        Assert.Null(await _queue.ClaimNextAsync());
        var version = _disk.Version;

        _free[low.Root] = long.MaxValue;
        Assert.Null(await _queue.ClaimNextAsync());

        _clock.Now = T0 + DownloadDiskHold.ReadInterval;
        Assert.Equal(low.ItemId, await _queue.ClaimNextAsync());
        Assert.NotEqual(version, _disk.Version);
    }

    [Fact]
    public async Task Free_space_is_read_once_per_interval_not_per_claim()
    {
        SeedItem();
        await _queue.ClaimNextAsync();
        await _queue.ClaimNextAsync();
        await _queue.ClaimNextAsync();

        Assert.Equal(1, _reads);
    }

    [Fact]
    public async Task A_disk_failure_makes_the_next_claim_read_again()
    {
        var low = SeedItem();
        await _queue.ClaimNextAsync();
        Assert.Empty(_disk.Current);
        using (var db = _db.NewContext())
        {
            await db.DownloadQueue.Where(q => q.Id == low.ItemId)
                .ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, QueueStatus.Queued));
        }

        _free[low.Root] = 1024;
        HealthWriteProbe.Shared.Expire();

        Assert.Null(await _queue.ClaimNextAsync());
        Assert.Single(_disk.Current);
    }

    [Fact]
    public async Task A_threshold_of_zero_never_holds_and_a_custom_one_is_used()
    {
        var off = SeedItem(sortOrder: 0, minFree: 0);
        var strict = SeedItem(sortOrder: 1, minFree: 100L * 1024 * 1024 * 1024);
        _free[off.Root] = 1;
        _free[strict.Root] = 50L * 1024 * 1024 * 1024;

        Assert.Equal(off.ItemId, await _queue.ClaimNextAsync());
        Assert.Null(await _queue.ClaimNextAsync());
        Assert.Equal(strict.Root, Assert.Single(_disk.Current).Path);
    }

    [Fact]
    public async Task A_root_whose_free_space_cannot_be_read_is_not_held()
    {
        var unknown = SeedItem();
        _free[unknown.Root] = null;

        Assert.Equal(unknown.ItemId, await _queue.ClaimNextAsync());
    }

    [Fact]
    public async Task A_torrent_grab_for_a_held_root_is_refused()
    {
        var low = SeedItem();
        var fine = SeedItem();
        _free[low.Root] = 1024;
        using var db = _db.NewContext();

        var refused = await Assert.ThrowsAsync<ReleaseRefusedException>(() => _disk.ThrowIfGrabHeldAsync(db, low.SeriesId));
        Assert.Equal(DownloadDiskHold.DiskLowKey, refused.Key);
        await _disk.ThrowIfGrabHeldAsync(db, fine.SeriesId);
    }
}
