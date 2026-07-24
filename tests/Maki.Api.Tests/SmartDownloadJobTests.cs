using Maki.Api.Jobs;
using Maki.Core.Configuration;
using Maki.Core.Entities;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="SmartDownloadJob.MonitorSmart(List{Chapter}, IAppSettings, CancellationToken)"/> is the
/// pure selection/monitoring step shared by the controller (initial pick when switching to Smart) and
/// the job (topping up as chapters get read). It must cap monitoring to the next batch, not just add to it.
/// </summary>
public class SmartDownloadJobTests
{
    private static Chapter Ch(int id, decimal number, int? fileId = null, bool monitored = false) =>
        new() { Id = id, Number = number, Language = "en", Monitored = monitored, ChapterFileId = fileId };

    [Fact]
    public async Task Picks_next_undownloaded_chapters_up_to_the_configured_count()
    {
        var settings = new FakeAppSettings().Set(SettingKeys.SmartDownloadChaptersCount, "2");
        var chapters = new List<Chapter> { Ch(1, 1m, fileId: 10), Ch(2, 2m), Ch(3, 3m), Ch(4, 4m) };

        var missing = await SmartDownloadJob.MonitorSmart(chapters, settings, CancellationToken.None);

        Assert.Equal([2, 3], missing);
        Assert.True(chapters.Single(c => c.Id == 2).Monitored);
        Assert.True(chapters.Single(c => c.Id == 3).Monitored);
        Assert.False(chapters.Single(c => c.Id == 4).Monitored);
    }

    [Fact]
    public async Task Skips_specials_when_unmonitor_specials_setting_is_on()
    {
        var settings = new FakeAppSettings()
            .Set(SettingKeys.SmartDownloadChaptersCount, "5")
            .Set(SettingKeys.MonitoringUnmonitorSpecials, "true");
        var chapters = new List<Chapter> { Ch(1, 1m), Ch(2, 1.5m), Ch(3, 2m) };

        var missing = await SmartDownloadJob.MonitorSmart(chapters, settings, CancellationToken.None);

        Assert.Equal([1, 3], missing);
        Assert.False(chapters.Single(c => c.Id == 2).Monitored);
    }

    [Fact]
    public async Task Already_downloaded_chapters_stay_monitored()
    {
        var settings = new FakeAppSettings().Set(SettingKeys.SmartDownloadChaptersCount, "1");
        var chapters = new List<Chapter> { Ch(1, 1m, fileId: 10), Ch(2, 2m), Ch(3, 3m) };

        await SmartDownloadJob.MonitorSmart(chapters, settings, CancellationToken.None);

        Assert.True(chapters.Single(c => c.Id == 1).Monitored);
        Assert.True(chapters.Single(c => c.Id == 2).Monitored);
        Assert.False(chapters.Single(c => c.Id == 3).Monitored);
    }
}

/// <summary>
/// <see cref="SmartDownloadJob.SeriesNeedingTopUpAsync"/> is the eligibility gate the job runs
/// before topping anything up: only a Smart series whose downloaded-but-unread backlog has shrunk
/// to within the configured limit is due, and only once reading progress exists at all.
/// </summary>
public class SeriesNeedingTopUpTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private int SeedDownloaded(int seriesId, params decimal[] numbers)
    {
        using var db = _db.NewContext();
        var fileId = 1000;
        foreach (var n in numbers)
        {
            var file = new ChapterFile { SeriesId = seriesId, RelativePath = $"ch-{n}.cbz", DateAdded = DateTime.UtcNow };
            db.ChapterFiles.Add(file);
            db.SaveChanges();
            db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = n, Language = "en", ChapterFileId = file.Id });
            fileId++;
        }
        db.SaveChanges();
        return seriesId;
    }

    private void SeedReadingState(int seriesId, double maxChapter)
    {
        using var db = _db.NewContext();
        db.ReadingStates.Add(new ReadingState
        {
            KavitaSeriesId = seriesId, SeriesId = seriesId, Title = "t", MaxChapter = maxChapter, UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private async Task<List<int>> Due(int limit = 5, bool skipSpecials = false)
    {
        using var db = _db.NewContext();
        var due = await SmartDownloadJob.SeriesNeedingTopUpAsync(db, limit, skipSpecials, CancellationToken.None);
        return due.Select(s => s.Id).ToList();
    }

    [Fact]
    public async Task Not_due_without_any_reading_progress()
    {
        var id = _db.SeedSeries(monitor: NewChapterMonitorMode.Smart);
        SeedDownloaded(id, 1m, 2m, 3m);

        Assert.DoesNotContain(id, await Due());
    }

    [Fact]
    public async Task Not_due_with_no_downloaded_chapters()
    {
        var id = _db.SeedSeries(monitor: NewChapterMonitorMode.Smart);
        SeedReadingState(id, maxChapter: 1);

        Assert.DoesNotContain(id, await Due());
    }

    [Fact]
    public async Task Due_when_unread_backlog_is_within_limit()
    {
        var id = _db.SeedSeries(monitor: NewChapterMonitorMode.Smart);
        SeedDownloaded(id, 1m, 2m, 3m, 4m, 5m);
        SeedReadingState(id, maxChapter: 3);

        Assert.Contains(id, await Due(limit: 2));
    }

    [Fact]
    public async Task Not_due_when_unread_backlog_exceeds_limit()
    {
        var id = _db.SeedSeries(monitor: NewChapterMonitorMode.Smart);
        SeedDownloaded(id, 1m, 2m, 3m, 4m, 5m);
        SeedReadingState(id, maxChapter: 1);

        Assert.DoesNotContain(id, await Due(limit: 2));
    }

    [Fact]
    public async Task Specials_excluded_from_backlog_when_setting_is_on()
    {
        var id = _db.SeedSeries(monitor: NewChapterMonitorMode.Smart);
        SeedDownloaded(id, 1m, 2m, 2.5m, 3m);
        SeedReadingState(id, maxChapter: 1);

        Assert.DoesNotContain(id, await Due(limit: 2, skipSpecials: false));
        Assert.Contains(id, await Due(limit: 2, skipSpecials: true));
    }

    [Fact]
    public async Task Non_smart_series_is_never_a_candidate()
    {
        var id = _db.SeedSeries(monitor: NewChapterMonitorMode.All);
        SeedDownloaded(id, 1m, 2m, 3m);
        SeedReadingState(id, maxChapter: 1);

        Assert.DoesNotContain(id, await Due());
    }
}
