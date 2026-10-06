using Maki.Api.Jobs;
using Maki.Core.Entities;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="SmartDownloadJob.SeriesNeedingTopUpAsync"/> is the eligibility gate the job runs
/// before topping anything up. A Smart series nobody has read is due only for its first batch; after
/// that, it is due once the downloaded-but-unread backlog past the reader has shrunk to the limit.
/// </summary>
public class SeriesNeedingTopUpTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private int SeedDownloaded(int seriesId, params decimal[] numbers) =>
        SeedDownloaded(seriesId, numbers, unwanted: []);

    private int SeedDownloaded(int seriesId, decimal[] numbers, decimal[] unwanted)
    {
        using var db = _db.NewContext();
        foreach (var n in numbers)
        {
            var file = new ChapterFile { SeriesId = seriesId, RelativePath = $"ch-{n}.cbz", DateAdded = DateTime.UtcNow };
            db.ChapterFiles.Add(file);
            db.SaveChanges();
            db.Chapters.Add(new Chapter
            {
                SeriesId = seriesId,
                Number = n,
                Language = "en",
                Wanted = !unwanted.Contains(n),
                ChapterFileId = file.Id,
            });
        }
        db.SaveChanges();
        return seriesId;
    }

    private void SeedReadingState(int seriesId, double maxChapter)
    {
        using var db = _db.NewContext();
        db.ReadingStates.Add(new ReadingState
        {
            UserId = 1,
            KavitaSeriesId = seriesId, SeriesId = seriesId, Title = "t", MaxChapter = maxChapter, UpdatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    private async Task<List<int>> Due(int limit = 5)
    {
        using var db = _db.NewContext();
        var due = await SmartDownloadJob.SeriesNeedingTopUpAsync(db, limit, CancellationToken.None);
        return due.Select(t => t.Series.Id).ToList();
    }

    [Fact]
    public async Task Not_due_without_reading_once_something_is_on_disk()
    {
        var id = _db.SeedSeries(monitor: NewChapterMonitorMode.Smart);
        SeedDownloaded(id, 1m, 2m, 3m);

        Assert.DoesNotContain(id, await Due());
    }

    /// <summary>
    /// A new Smart series used to wait for a reading state and a downloaded chapter before its first
    /// batch, so one added and never touched by hand downloaded nothing at all.
    /// </summary>
    [Fact]
    public async Task A_new_series_with_nothing_read_or_downloaded_gets_its_first_batch()
    {
        var id = _db.SeedSeries(monitor: NewChapterMonitorMode.Smart);

        using var db = _db.NewContext();
        var due = Assert.Single(await SmartDownloadJob.SeriesNeedingTopUpAsync(db, 5, CancellationToken.None));
        Assert.Equal(id, due.Series.Id);
        Assert.Null(due.After);
    }

    [Fact]
    public async Task Due_with_reading_and_nothing_downloaded_starting_after_the_reader()
    {
        var id = _db.SeedSeries(monitor: NewChapterMonitorMode.Smart);
        SeedReadingState(id, maxChapter: 40);

        using var db = _db.NewContext();
        var due = Assert.Single(await SmartDownloadJob.SeriesNeedingTopUpAsync(db, 5, CancellationToken.None));
        Assert.Equal(id, due.Series.Id);
        Assert.Equal(40m, due.After);
    }

    [Fact]
    public void A_batch_skips_chapters_at_or_below_the_reader()
    {
        Chapter Ch(int id, decimal? n) => new() { Id = id, Number = n, Language = "en" };
        Chapter[] chapters = [Ch(1, 1m), Ch(2, 40m), Ch(3, 41m), Ch(4, 42m), Ch(5, null)];

        Assert.Equal([3, 4], Chapter.NextWanted(SmartDownloadJob.Ahead(chapters, 40m), 2));
        Assert.Equal([1, 2], Chapter.NextWanted(SmartDownloadJob.Ahead(chapters, null), 2));
        Assert.Equal([3, 4, 5], Chapter.NextWanted(SmartDownloadJob.Ahead(chapters, 40m), 10));
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

    /// <summary>
    /// Wanted is the only eligibility rule now, backlog included. A chapter the user doesn't want
    /// sitting downloaded-and-unread would otherwise hold the series permanently over the limit and
    /// silently stop every future top-up.
    /// </summary>
    [Fact]
    public async Task Unwanted_chapters_are_excluded_from_the_backlog()
    {
        var id = _db.SeedSeries(monitor: NewChapterMonitorMode.Smart);
        SeedDownloaded(id, [1m, 2m, 2.5m, 3m], unwanted: []);
        SeedReadingState(id, maxChapter: 1);
        Assert.DoesNotContain(id, await Due(limit: 2));

        var other = _db.SeedSeries(monitor: NewChapterMonitorMode.Smart);
        SeedDownloaded(other, [1m, 2m, 2.5m, 3m], unwanted: [2.5m]);
        SeedReadingState(other, maxChapter: 1);
        Assert.Contains(other, await Due(limit: 2));
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
