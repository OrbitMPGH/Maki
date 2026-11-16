using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;

namespace Maki.Api.Tests;

public class SeriesReadingStatsServiceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private sealed class UtcSettingsStore : IUserSettingsStore
    {
        public Task<string?> GetAsync(int userId, string key, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task SetAsync(int userId, string key, string? value, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private async Task<SeriesReadingStats?> StatsFor(int userId, int seriesId, string? type = SeriesTypes.Manga)
    {
        using var db = _db.NewContext(userId);
        return await new SeriesReadingStatsService(db, new UtcSettingsStore()).ForAsync(userId, seriesId, type, Now);
    }

    [Fact]
    public async Task A_series_read_outside_the_built_in_reader_has_no_stats()
    {
        var user = _db.SeedUser();
        var series = _db.SeedSeries("Read in Kavita");
        SeedChapters(user, series, [0, 0, 0]);

        Assert.Null(await StatsFor(user, series));
    }

    [Fact]
    public async Task Totals_pace_and_the_comparison_come_from_timed_chapters()
    {
        var user = _db.SeedUser();
        var series = _db.SeedSeries("Target");
        var other = _db.SeedSeries("Other manga");
        var webtoon = _db.SeedSeries("Webtoon", configure: s => s.Type = SeriesTypes.Manhwa);
        SeedChapters(user, series, [300, 420, 480, 0]);
        SeedChapters(user, other, [200, 240, 260]);
        SeedChapters(user, webtoon, [900, 900, 900]);

        var stats = await StatsFor(user, series);

        Assert.NotNull(stats);
        Assert.Equal(1_200, stats.Seconds);
        Assert.Equal(3, stats.TimedChapters);
        Assert.Equal(420, stats.ChapterMedianSeconds);
        Assert.Equal(240, stats.ElsewhereMedianSeconds);
    }

    [Fact]
    public async Task Another_users_time_is_not_counted()
    {
        var reader = _db.SeedUser("reader");
        var other = _db.SeedUser("other");
        var series = _db.SeedSeries("Shared");
        SeedChapters(other, series, [600, 600, 600]);

        Assert.Null(await StatsFor(reader, series));
    }

    [Fact]
    public async Task Sittings_split_on_a_gap_longer_than_ten_minutes()
    {
        var user = _db.SeedUser();
        var series = _db.SeedSeries("Target");
        SeedChapters(user, series, [900]);
        var start = Now.AddDays(-2);
        SeedTime(user, series, start.AddMinutes(5), 300);
        SeedTime(user, series, start.AddMinutes(10), 300);
        SeedTime(user, series, start.AddMinutes(40), 300);
        SeedTime(user, series, start.AddDays(1), 300);

        var stats = await StatsFor(user, series);

        Assert.NotNull(stats);
        Assert.Equal(3, stats.Sittings);
        Assert.Equal(start, stats.FirstReadAt);
    }

    [Fact]
    public async Task A_short_history_is_padded_to_eight_weeks_ending_this_week()
    {
        var user = _db.SeedUser();
        var series = _db.SeedSeries("Target");
        SeedChapters(user, series, [600]);
        SeedTime(user, series, Now.AddDays(-8), 600);
        SeedTime(user, series, Now.AddHours(-1), 300);

        var stats = await StatsFor(user, series);

        Assert.NotNull(stats);
        Assert.Equal("week", stats.BucketUnit);
        Assert.Equal(8, stats.Timeline.Count);
        Assert.Equal("2026-11-09", stats.Timeline[^1].Bucket);
        Assert.Equal(300, stats.Timeline[^1].Seconds);
        Assert.Equal(600, stats.Timeline[^2].Seconds);
    }

    [Fact]
    public void A_long_history_is_bucketed_by_month()
    {
        var (unit, buckets) = SeriesReadingStatsService.Timeline(
            [(Now.AddDays(-400), 600), (Now.AddDays(-30), 120)], TimeZoneInfo.Utc, Now);

        Assert.Equal("month", unit);
        Assert.Equal(14, buckets.Count);
        Assert.Equal("2025-10", buckets[0].Bucket);
        Assert.Equal(600, buckets[0].Seconds);
        Assert.Equal("2026-11", buckets[^1].Bucket);
    }

    [Fact]
    public async Task A_fully_incognito_series_still_shows_its_total_without_a_timeline()
    {
        var user = _db.SeedUser();
        var series = _db.SeedSeries("Private");
        SeedChapters(user, series, [600, 600]);

        var stats = await StatsFor(user, series);

        Assert.NotNull(stats);
        Assert.Equal(1_200, stats.Seconds);
        Assert.Equal(0, stats.Sittings);
        Assert.Empty(stats.Timeline);
    }

    private void SeedChapters(int userId, int seriesId, int[] seconds)
    {
        using var db = _db.NewContext();
        for (var index = 0; index < seconds.Length; index++)
        {
            var chapter = new Chapter { SeriesId = seriesId, Number = index + 1 };
            db.Chapters.Add(chapter);
            db.SaveChanges();
            db.ChapterProgress.Add(new ChapterProgress
            {
                UserId = userId,
                SeriesId = seriesId,
                ChapterId = chapter.Id,
                Completed = true,
                ReadSeconds = seconds[index],
                PageCount = 20,
                StartedAt = Now.AddDays(-3),
                UpdatedAt = Now.AddDays(-3).AddMinutes(index),
            });
        }

        db.SaveChanges();
    }

    private void SeedTime(int userId, int seriesId, DateTime at, int seconds)
    {
        using var db = _db.NewContext();
        db.StatsEvents.Add(new StatsEvent
        {
            Type = StatsEventType.ReadingTime,
            Timestamp = at,
            UserId = userId,
            SeriesId = seriesId,
            SeriesTitle = "Series",
            Value = seconds,
        });
        db.SaveChanges();
    }
}
