using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Reading;

namespace Maki.Api.Tests;

public class ReadingTimeEstimateServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Scrolling_and_paged_history_stay_separate()
    {
        var scrollingHistory = _db.SeedSeries("Scroll history", configure: s => s.Type = SeriesTypes.Manhua);
        var pagedHistory = _db.SeedSeries("Paged history", configure: s => s.Type = SeriesTypes.Manga);
        var scrollingTarget = _db.SeedSeries("Scroll target", configure: s => s.Type = SeriesTypes.Manhua);
        var pagedTarget = _db.SeedSeries("Paged target", configure: s => s.Type = SeriesTypes.Manga);
        SeedTimedChapters(scrollingHistory, 600, 620, 580);
        SeedTimedChapters(pagedHistory, 300, 320, 280);

        using var db = _db.NewContext(1);
        var service = new ReadingTimeEstimateService(db);

        var scrolling = await service.EstimateAsync(
            scrollingTarget, totalChapters: 10, readChapters: 2, ReaderPrefsSpec.ModeVertical);
        var paged = await service.EstimateAsync(
            pagedTarget, totalChapters: 10, readChapters: 2, ReaderPrefsSpec.ModePaged);

        Assert.NotNull(scrolling);
        Assert.Equal("scrolling", scrolling.Style);
        Assert.Equal(4_800, scrolling.Seconds);
        Assert.Equal(8, scrolling.RemainingChapters);
        Assert.False(scrolling.SeriesSpecific);

        Assert.NotNull(paged);
        Assert.Equal("paged", paged.Style);
        Assert.Equal(2_400, paged.Seconds);
        Assert.False(paged.SeriesSpecific);
    }

    [Fact]
    public async Task Series_history_wins_over_the_general_style()
    {
        var general = _db.SeedSeries("General", configure: s => s.Type = SeriesTypes.Manga);
        var target = _db.SeedSeries("Target", configure: s => s.Type = SeriesTypes.Manga);
        SeedTimedChapters(general, 300, 300, 300);
        SeedTimedChapters(target, 120, 180, 240);

        using var db = _db.NewContext(1);
        var estimate = await new ReadingTimeEstimateService(db).EstimateAsync(
            target, totalChapters: 8, readChapters: 3, ReaderPrefsSpec.ModePaged);

        Assert.NotNull(estimate);
        Assert.Equal(900, estimate.Seconds);
        Assert.Equal(3, estimate.SampleChapters);
        Assert.True(estimate.SeriesSpecific);
    }

    [Fact]
    public async Task Too_little_history_produces_no_guess()
    {
        var target = _db.SeedSeries("Target", configure: s => s.Type = SeriesTypes.Manga);
        SeedTimedChapters(target, 120, 180);

        using var db = _db.NewContext(1);
        var estimate = await new ReadingTimeEstimateService(db).EstimateAsync(
            target, totalChapters: 8, readChapters: 2, ReaderPrefsSpec.ModePaged);

        Assert.Null(estimate);
    }

    [Fact]
    public async Task Watched_and_untimed_chapters_are_not_pace_samples()
    {
        var target = _db.SeedSeries("Target", configure: s => s.Type = SeriesTypes.Manhua);
        SeedTimedChapters(target, [0, 300, 600, 900], watchedIndex: 3);

        using var db = _db.NewContext(1);
        var estimate = await new ReadingTimeEstimateService(db).EstimateAsync(
            target, totalChapters: 8, readChapters: 4, ReaderPrefsSpec.ModeVertical);

        Assert.Null(estimate);
    }

    private void SeedTimedChapters(
        int seriesId,
        params int[] seconds) =>
        SeedTimedChapters(seriesId, seconds, watchedIndex: -1);

    private void SeedTimedChapters(int seriesId, int[] seconds, int watchedIndex)
    {
        using var db = _db.NewContext();
        for (var index = 0; index < seconds.Length; index++)
        {
            var chapter = new Chapter { SeriesId = seriesId, Number = index + 1 };
            db.Chapters.Add(chapter);
            db.SaveChanges();
            db.ChapterProgress.Add(new ChapterProgress
            {
                UserId = 1,
                SeriesId = seriesId,
                ChapterId = chapter.Id,
                Completed = true,
                Watched = index == watchedIndex,
                ReadSeconds = seconds[index],
                PageCount = 20,
                StartedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow.AddMinutes(index),
            });
        }

        db.SaveChanges();
    }
}
