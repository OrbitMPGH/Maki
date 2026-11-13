using Maki.Core.Sources;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Data.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// When a read makes a series Completed on its own, and when Maki asks instead: switched-off chapters
/// may be skipped extras or the rest of a series somebody is trying out, so they never complete it.
/// </summary>
public sealed class SeriesCompletionTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly int _user;

    public SeriesCompletionTests() => _user = _db.SeedUser();

    public void Dispose() => _db.Dispose();

    private ReadingProgressService NewService() =>
        new(_db.NewContext(), new ReadingProgressGate(), NullLogger<ReadingProgressService>.Instance,
            new FakeAppSettings());

    private int SeedSeries(int? total) =>
        _db.SeedSeries("Siren", configure: s =>
        {
            s.Status = SeriesStatus.Completed;
            s.TotalChapters = total;
        });

    private int[] SeedChapters(int seriesId, IEnumerable<decimal> numbers, bool downloaded)
    {
        using var db = _db.NewContext();
        var chapters = numbers.Select(n => new Chapter
        {
            SeriesId = seriesId,
            Number = n,
            Language = "en",
            Wanted = downloaded,
            ChapterFile = downloaded
                ? new ChapterFile { SeriesId = seriesId, RelativePath = $"{n}.cbz", DateAdded = DateTime.UtcNow }
                : null,
        }).ToList();
        db.Chapters.AddRange(chapters);
        db.SaveChanges();
        return chapters.Select(c => c.Id).ToArray();
    }

    private void Read(int seriesId, IEnumerable<int> chapterIds)
    {
        using var db = _db.NewContext();
        db.ChapterProgress.AddRange(chapterIds.Select(id => new ChapterProgress
        {
            UserId = _user, SeriesId = seriesId, ChapterId = id, Completed = true, PageCount = 20, PageIndex = 19,
        }));
        db.SaveChanges();
    }

    private ReadingStatus? Status(int seriesId) =>
        _db.NewContext().UserSeriesStates.IgnoreQueryFilters()
            .Where(s => s.UserId == _user && s.SeriesId == seriesId)
            .Select(s => (ReadingStatus?)s.Status)
            .FirstOrDefault();

    private Task<int?> AskAsync(int seriesId, bool expectsMore = false)
    {
        var db = _db.NewContext();
        return SeriesCompletion.AskAsync(
            db, _user, db.Series.Single(s => s.Id == seriesId), expectsMore, unmonitorSpecials: false, default);
    }

    [Fact]
    public async Task Reading_every_main_chapter_completes_a_series_whose_skipped_specials_MangaBaka_counts()
    {
        var series = SeedSeries(29);
        var main = SeedChapters(series, Enumerable.Range(1, 26).Select(n => (decimal)n), downloaded: true);
        SeedChapters(series, [7.1m, 7.5m, 13.5m], downloaded: false);
        Read(series, main);

        await NewService().TrackNativeAsync(_user, series, "Siren", 26, 0, default);

        Assert.Equal(ReadingStatus.Completed, Status(series));
    }

    [Fact]
    public async Task Filling_in_earlier_chapters_after_the_last_one_completes_it()
    {
        var series = SeedSeries(10);
        var chapters = SeedChapters(series, Enumerable.Range(1, 10).Select(n => (decimal)n), downloaded: true);
        Read(series, [chapters[9]]);
        await NewService().TrackNativeAsync(_user, series, "Siren", 10, 0, default);
        Assert.Equal(ReadingStatus.Reading, Status(series));

        Read(series, chapters[..9]);
        await NewService().TrackNativeAsync(_user, series, "Siren", 5, 0, default);

        Assert.Equal(ReadingStatus.Completed, Status(series));
    }

    [Fact]
    public async Task Trying_out_the_first_chapters_never_completes_it_and_asks_nothing_until_they_are_read()
    {
        var series = SeedSeries(null);
        var kept = SeedChapters(series, [1m, 2m, 3m], downloaded: true);
        SeedChapters(series, Enumerable.Range(4, 97).Select(n => (decimal)n), downloaded: false);
        Read(series, kept[..2]);
        Assert.Null(await AskAsync(series));

        Read(series, [kept[2]]);
        await NewService().TrackNativeAsync(_user, series, "Siren", 3, 0, default);

        Assert.Equal(ReadingStatus.Reading, Status(series));
        Assert.Equal(97, await AskAsync(series));
    }

    [Fact]
    public async Task A_source_missing_the_final_chapters_does_not_complete_it()
    {
        var series = SeedSeries(100);
        Read(series, SeedChapters(series, Enumerable.Range(1, 80).Select(n => (decimal)n), downloaded: true));

        await NewService().TrackNativeAsync(_user, series, "Siren", 80, 0, default);

        Assert.True(_db.NewContext().ReadingStates.IgnoreQueryFilters().Single().Finished);
        Assert.Equal(ReadingStatus.Reading, Status(series));
        Assert.Null(await AskAsync(series));
    }

    [Fact]
    public async Task A_series_still_publishing_is_never_asked_about()
    {
        var series = _db.SeedSeries("Ongoing", configure: s => s.Status = SeriesStatus.Ongoing);
        Read(series, SeedChapters(series, [1m, 2m], downloaded: true));
        SeedChapters(series, [3m], downloaded: false);

        Assert.Null(await AskAsync(series));
    }

    [Fact]
    public async Task Not_yet_stops_the_ask()
    {
        var series = SeedSeries(null);
        Read(series, SeedChapters(series, [1m], downloaded: true));
        SeedChapters(series, [2m], downloaded: false);
        Assert.Equal(1, await AskAsync(series));

        using (var db = _db.NewContext())
        {
            var state = db.UserSeriesStates.IgnoreQueryFilters().Single(s => s.UserId == _user && s.SeriesId == series);
            state.CompleteHintDismissedAt = DateTime.UtcNow;
            db.SaveChanges();
        }

        Assert.Null(await AskAsync(series));
    }

    [Fact]
    public async Task A_next_chapter_on_the_way_stops_the_ask()
    {
        var series = SeedSeries(null);
        Read(series, SeedChapters(series, [1m], downloaded: true));
        SeedChapters(series, [2m], downloaded: false);

        Assert.Null(await AskAsync(series, expectsMore: true));
    }

    [Fact]
    public async Task A_finished_series_left_at_Reading_is_asked_about_with_nothing_left_out()
    {
        // Read to the end while a chapter was still expected, so the automatic step held back.
        var series = SeedSeries(3);
        Read(series, SeedChapters(series, [1m, 2m, 3m], downloaded: true));
        using (var db = _db.NewContext())
        {
            db.ReadingStates.Add(new ReadingState { UserId = _user, SeriesId = series, Title = "Siren", MaxChapter = 3, Finished = true });
            db.SaveChanges();
        }

        Assert.Equal(0, await AskAsync(series));
    }

    [Fact]
    public async Task The_end_screen_counts_the_chapter_it_is_on_as_read()
    {
        var series = SeedSeries(null);
        var kept = SeedChapters(series, [1m, 2m], downloaded: true);
        SeedChapters(series, [3m], downloaded: false);
        Read(series, [kept[0]]);

        var db = _db.NewContext();
        var left = await SeriesCompletion.AskAsync(
            db, _user, db.Series.Single(s => s.Id == series), false, false, default,
            db.Chapters.Single(c => c.Id == kept[1]));

        Assert.Equal(1, left);
    }

    [Fact]
    public async Task The_backfill_completes_finished_series_but_keeps_a_status_set_by_hand()
    {
        var done = SeedSeries(29);
        var main = SeedChapters(done, Enumerable.Range(1, 26).Select(n => (decimal)n), downloaded: true);
        SeedChapters(done, [7.1m, 7.5m, 13.5m], downloaded: false);
        Read(done, main);
        var held = SeedSeries(3);
        Read(held, SeedChapters(held, [1m, 2m, 3m], downloaded: true));
        using (var db = _db.NewContext())
        {
            db.ReadingStates.AddRange(
                new ReadingState { UserId = _user, SeriesId = done, Title = "Siren", MaxChapter = 26, Finished = true },
                new ReadingState { UserId = _user, SeriesId = held, Title = "Held", MaxChapter = 3, Finished = true });
            db.UserSeriesStates.IgnoreQueryFilters().Single(s => s.SeriesId == held).Status = ReadingStatus.OnHold;
            db.SaveChanges();
        }

        var registry = new SourceRegistry([]);
        var settings = new FakeAppSettings();
        var db2 = _db.NewContext();
        var outlooks = new ReleaseOutlookService(
            db2, registry, new SourceAvailability(settings, registry), settings, TimeProvider.System);
        await new SeriesCompletionBackfillService(db2, settings, outlooks,
            NullLogger<SeriesCompletionBackfillService>.Instance).RunOnceAsync();

        Assert.Equal(ReadingStatus.Completed, Status(done));
        Assert.Equal(ReadingStatus.OnHold, Status(held));
    }
}
