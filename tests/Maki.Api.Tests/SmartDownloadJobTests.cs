using Maki.Api.Jobs;
using Maki.Core.Entities;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="SmartDownloadJob.SeriesNeedingTopUpAsync"/> is the eligibility gate the job runs
/// before topping anything up: only a Smart series whose downloaded-but-unread backlog has shrunk
/// to within the configured limit is due, and only once reading progress exists at all.
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
