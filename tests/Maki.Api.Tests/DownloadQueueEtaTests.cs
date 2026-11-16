using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;

namespace Maki.Api.Tests;

/// <summary>The queue's time-left estimate: what it is built from and when it stays quiet.</summary>
public class DownloadQueueEtaTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(Now);
    }

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    /// <summary>Back to back, one at a time: 20 pages in 2 minutes each, so 10 pages a minute.</summary>
    private static List<DownloadQueueEta.Sample> Serial(string source, int count, int pages = 20, double minutes = 2) =>
        Enumerable.Range(0, count)
            .Select(i => new DownloadQueueEta.Sample(source, pages,
                Now.AddHours(-1).AddMinutes(i * minutes), Now.AddHours(-1).AddMinutes((i + 1) * minutes)))
            .ToList();

    private static DownloadQueueEta.Work Row(int series, string? source, int? pages = null) =>
        new(series, source, pages, InFlight: false, NotBefore: null);

    [Fact]
    public void Too_little_history_gives_no_estimate()
    {
        var result = DownloadQueueEta.Simulate(Serial("a", DownloadQueueEta.MinPooledSamples - 1), [Row(1, "a")], 2, Now);

        Assert.Null(result);
    }

    [Fact]
    public void One_worker_adds_the_chapters_up()
    {
        var result = DownloadQueueEta.Simulate(Serial("a", 6), [Row(1, "a"), Row(1, "a"), Row(2, "a", pages: 10)], 1, Now)!;

        Assert.Equal(4 * 60, result[1], 3);
        Assert.Equal(5 * 60, result[2], 3);
    }

    [Fact]
    public void A_source_seen_one_at_a_time_is_not_assumed_to_run_in_parallel()
    {
        var result = DownloadQueueEta.Simulate(Serial("a", 6), [Row(1, "a"), Row(1, "a")], 4, Now)!;

        Assert.Equal(4 * 60, result[1], 3);
    }

    [Fact]
    public void Two_sources_share_the_workers_side_by_side()
    {
        var samples = Serial("a", 6).Concat(Serial("b", 6, minutes: 4)).ToList();

        var result = DownloadQueueEta.Simulate(samples, [Row(1, "a"), Row(2, "b")], 2, Now)!;

        Assert.Equal(2 * 60, result[1], 3);
        Assert.Equal(4 * 60, result[2], 3);
    }

    [Fact]
    public void A_source_with_thin_history_falls_back_to_the_pooled_rate()
    {
        var samples = Serial("a", 6);

        var result = DownloadQueueEta.Simulate(samples, [Row(1, "new"), Row(2, null)], 2, Now)!;

        Assert.Equal(2 * 60, result[1], 3);
        Assert.Equal(2 * 60, result[2], 3);
    }

    [Fact]
    public void A_rate_limited_row_waits_for_its_next_attempt()
    {
        var row = new DownloadQueueEta.Work(1, "a", 20, InFlight: false, NotBefore: Now.AddMinutes(10));

        var result = DownloadQueueEta.Simulate(Serial("a", 6), [row], 1, Now)!;

        Assert.Equal(12 * 60, result[1], 3);
    }

    [Fact]
    public async Task Held_rows_are_left_out_and_their_series_reads_as_held()
    {
        var settings = new FakeAppSettings();
        settings.Set(SettingKeys.DownloadConcurrentChapters, "1");
        var pauses = new DownloadPauseService(settings, new Clock());
        var eta = new DownloadQueueEta(_db.ScopeFactory(), new Clock(), settings, pauses);

        var mapping = () => new SourceMapping { SourceName = "a", SourceSeriesId = "s", Url = "https://a.test", Priority = 1 };
        var running = _db.SeedSeries("Running", mappings: [mapping()]);
        var paused = _db.SeedSeries("Paused", mappings: [mapping()]);
        using (var db = _db.NewContext())
        {
            var mappingId = db.SourceMappings.First(m => m.SeriesId == running).Id;
            foreach (var sample in Serial("a", 6))
            {
                db.DownloadQueue.Add(new DownloadQueueItem
                {
                    SeriesId = running, ChapterId = null, Protocol = AcquisitionProtocol.Scraper,
                    Status = QueueStatus.Completed, SourceMappingId = mappingId, PagesTotal = sample.Pages,
                    PagesDone = sample.Pages, StartedAt = sample.Start, CompletedAt = sample.End, QueuedAt = sample.Start,
                });
            }

            foreach (var seriesId in new[] { running, paused })
            {
                var chapter = db.Chapters.Add(new Chapter { SeriesId = seriesId, Number = 1m, Language = "en" }).Entity;
                db.SaveChanges();
                db.DownloadQueue.Add(new DownloadQueueItem
                {
                    SeriesId = seriesId, ChapterId = chapter.Id, Protocol = AcquisitionProtocol.Scraper,
                    Status = QueueStatus.Queued, SourceMappingId = db.SourceMappings.First(m => m.SeriesId == seriesId).Id,
                    QueuedAt = Now,
                });
            }

            db.SaveChanges();
        }

        await pauses.PauseSeriesAsync(paused, null);
        using var request = _db.NewContext();
        var result = await eta.EstimateAsync(request);

        Assert.Equal(2 * 60, result.QueueSeconds!.Value, 3);
        Assert.Equal(2 * 60, result.Series.Single(s => s.SeriesId == running).Seconds!.Value, 3);
        var held = result.Series.Single(s => s.SeriesId == paused);
        Assert.True(held.Held);
        Assert.Null(held.Seconds);
    }
}
