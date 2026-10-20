using Maki.Api.Jobs;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Releases;
using Maki.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// First-seen stamping and the per-series schedule written by a full sync, plus the job's
/// selection of due series. The tier rules themselves are covered in <c>RefreshScheduleTests</c>.
/// </summary>
public class ReleaseAwareRefreshTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private ChapterSyncService Service(FakeSource source, FakeAppSettings? settings = null) =>
        new(
            _db.NewContext(),
            new SourceRegistry([source]),
            new DownloadQueueService(null!, TimeProvider.System, null!, NullLogger<DownloadQueueService>.Instance),
            Sources.AllEnabled,
            new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance),
            settings ?? new FakeAppSettings(),
            NullLogger<ChapterSyncService>.Instance);

    private static SourceMapping Mapping() => new() { SourceName = "fake", SourceSeriesId = "series", Url = "u" };

    private Dictionary<decimal, DateTime?> FirstSeen(int seriesId)
    {
        using var db = _db.NewContext();
        return db.ChapterSourceLinks
            .Where(l => l.Chapter!.SeriesId == seriesId)
            .Select(l => new { l.Chapter!.Number, l.FirstSeenAt })
            .ToList()
            .ToDictionary(x => x.Number!.Value, x => x.FirstSeenAt);
    }

    private Series SeriesRow(int id)
    {
        using var db = _db.NewContext();
        return db.Series.Single(s => s.Id == id);
    }

    [Fact]
    public async Task The_first_snapshot_stamps_nothing_and_a_later_arrival_is_stamped()
    {
        var id = _db.SeedSeries(mappings: Mapping());
        var chapters = new List<decimal> { 1, 2, 3 };
        var source = new FakeSource { Name = "fake" };
        source = new FakeSource { Name = "fake", OnListChapters = _ => chapters.Select(n => source.Chapter(n)).ToList() };

        await Service(source).SyncSeriesAsync(id);
        Assert.All(FirstSeen(id).Values, Assert.Null);

        chapters.Add(4);
        var before = DateTime.UtcNow;
        await Service(source).SyncSeriesAsync(id);

        var stamps = FirstSeen(id);
        Assert.Null(stamps[3]);
        Assert.NotNull(stamps[4]);
        Assert.InRange(stamps[4]!.Value, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task An_older_chapter_a_source_adds_later_is_not_a_release()
    {
        var id = _db.SeedSeries(mappings: Mapping());
        var chapters = new List<decimal> { 1, 5 };
        var source = new FakeSource { Name = "fake" };
        source = new FakeSource { Name = "fake", OnListChapters = _ => chapters.Select(n => source.Chapter(n)).ToList() };
        await Service(source).SyncSeriesAsync(id);

        chapters.Add(3);
        await Service(source).SyncSeriesAsync(id);

        // New to the series, so it is a release after all: nothing else listed it before.
        Assert.NotNull(FirstSeen(id)[3]);

        using (var db = _db.NewContext())
        {
            // A second mapping that already listed 1-5 adds the gap chapter 2 only now.
            db.Chapters.Add(new Chapter { SeriesId = id, Number = 2, Language = "en" });
            db.SaveChanges();
        }

        chapters.Add(2);
        await Service(source).SyncSeriesAsync(id);

        Assert.Null(FirstSeen(id)[2]);
    }

    [Fact]
    public async Task A_catalogue_arriving_at_once_is_not_stamped()
    {
        var id = _db.SeedSeries(mappings: Mapping());
        var count = 1;
        var source = new FakeSource { Name = "fake" };
        source = new FakeSource
        {
            Name = "fake",
            OnListChapters = _ => Enumerable.Range(1, count).Select(n => source.Chapter(n)).ToList()
        };
        await Service(source).SyncSeriesAsync(id);

        count = 2 + ChapterSyncService.MaxArrivalsPerSync;
        await Service(source).SyncSeriesAsync(id);

        Assert.All(FirstSeen(id).Values, Assert.Null);
    }

    [Fact]
    public async Task A_full_sync_schedules_the_next_check_from_the_newest_chapter()
    {
        var id = _db.SeedSeries(mappings: Mapping());
        var source = new FakeSource { Name = "fake" };
        source = new FakeSource
        {
            Name = "fake",
            OnListChapters = _ => [source.Chapter(1, releaseDate: DateTime.UtcNow.AddDays(-5))]
        };

        var before = DateTime.UtcNow;
        await Service(source).SyncSeriesAsync(id);

        var series = SeriesRow(id);
        Assert.InRange(series.LastCheckAt!.Value, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
        Assert.Equal(RefreshSchedule.ActiveInterval, series.NextCheckAt - series.LastCheckAt);
    }

    [Fact]
    public async Task Untrusted_dates_alone_do_not_make_a_quiet_series_look_active()
    {
        var id = _db.SeedSeries(mappings: Mapping());
        var source = new FakeSource { Name = "fake" };
        source = new FakeSource
        {
            Name = "fake",
            ReleaseDatesAreReliable = false,
            OnListChapters = _ => [source.Chapter(1, releaseDate: DateTime.UtcNow.AddDays(-200))]
        };

        await Service(source).SyncSeriesAsync(id);

        var series = SeriesRow(id);
        Assert.Equal(RefreshSchedule.QuietInterval, series.NextCheckAt - series.LastCheckAt);
    }

    [Fact]
    public async Task An_announced_release_is_stored_and_pulls_the_check_forward()
    {
        var id = _db.SeedSeries(mappings: Mapping());
        var announced = DateTime.UtcNow.AddHours(1);
        var source = new FakeSource { Name = "fake" };
        source = new FakeSource
        {
            Name = "fake",
            NextRelease = announced,
            OnListChapters = _ => [source.Chapter(1, releaseDate: DateTime.UtcNow.AddDays(-30))]
        };

        await Service(source).SyncSeriesAsync(id);

        using var db = _db.NewContext();
        Assert.Equal(announced, db.SourceMappings.Single(m => m.SeriesId == id).AnnouncedNextReleaseAt);
        Assert.Equal(announced + RefreshSchedule.AnnouncedSlack, SeriesRow(id).NextCheckAt!.Value, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task With_adaptive_refresh_off_every_series_is_due_every_thirty_minutes()
    {
        var id = _db.SeedSeries(mappings: Mapping());
        var settings = new FakeAppSettings();
        await settings.SetAsync(SettingKeys.MonitoringAdaptiveRefresh, "false");
        var source = new FakeSource { Name = "fake" };
        source = new FakeSource
        {
            Name = "fake",
            OnListChapters = _ => [source.Chapter(1, releaseDate: DateTime.UtcNow.AddDays(-400))]
        };

        await Service(source, settings).SyncSeriesAsync(id);

        var series = SeriesRow(id);
        Assert.Equal(RefreshSchedule.Floor, series.NextCheckAt - series.LastCheckAt);
    }

    [Fact]
    public async Task A_partial_sync_leaves_the_schedule_alone()
    {
        var id = _db.SeedSeries(mappings: Mapping());
        var source = new FakeSource { Name = "fake" };
        source = new FakeSource { Name = "fake", OnListChapters = _ => [source.Chapter(1)] };
        int mappingId;
        using (var db = _db.NewContext())
        {
            mappingId = db.SourceMappings.Single(m => m.SeriesId == id).Id;
        }

        await Service(source).SyncMappingsAsync(id, [mappingId]);

        Assert.Null(SeriesRow(id).NextCheckAt);
    }

    [Fact]
    public async Task Only_due_series_are_picked_and_the_most_overdue_go_first()
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var never = _db.SeedSeries(title: "Never", mappings: Mapping());
        var overdue = _db.SeedSeries(title: "Overdue", configure: s => s.NextCheckAt = now.AddHours(-3), mappings: Mapping());
        var due = _db.SeedSeries(title: "Due", configure: s => s.NextCheckAt = now.AddMinutes(-1), mappings: Mapping());
        var later = _db.SeedSeries(title: "Later", configure: s => s.NextCheckAt = now.AddHours(5), mappings: Mapping());

        using var db = _db.NewContext();
        var picked = await RefreshMonitoredSeriesJob.DueSeriesIdsAsync(db, [], now);

        Assert.Equal([never, overdue, due], picked);
        Assert.DoesNotContain(later, picked);
    }
}
