using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Progress;
using Maki.Data;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The metrics recompute and the achievement evaluator.
/// <para>
/// The load-bearing guarantee is the first test here: every counter is derived from the
/// <c>StatsEvents</c> log, which is what makes <c>IncognitoMode.Full</c> reading invisible to
/// progression without a second gate to keep in step. Everything else follows from that choice —
/// idempotency, because the evaluator runs on both the completion path and every page load, and
/// stickiness, because a derived metric can move backwards while an unlock must not.
/// </para>
/// </summary>
public sealed class ProgressTests : IDisposable
{
    private const int UserId = 1;
    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private sealed class StoppedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private readonly TestDb _db = new();
    private readonly StoppedClock _clock = new(Now);
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public ProgressTests() => _db.SeedUser();

    public void Dispose()
    {
        _cache.Dispose();
        _db.Dispose();
    }

    private UserMetricsService Metrics(MakiDbContext? db = null) =>
        new(db ?? _db.NewContext(), new TestUserSettingsStore(_db), _cache, _clock);

    private AchievementService Achievements(MakiDbContext db) =>
        new(db, Metrics(db), new TestUserSettingsStore(_db), _clock,
            NullLogger<AchievementService>.Instance);

    /// <summary>Reads and writes the real UserSettings rows, so the master switch is exercised for real.</summary>
    private sealed class TestUserSettingsStore(TestDb db) : IUserSettingsStore
    {
        public Task<string?> GetAsync(int userId, string key, CancellationToken ct = default)
        {
            using var context = db.NewContext();
            return Task.FromResult(context.UserSettings
                .Where(s => s.UserId == userId && s.Key == key)
                .Select(s => s.Value)
                .FirstOrDefault());
        }

        public Task SetAsync(int userId, string key, string? value, CancellationToken ct = default)
        {
            using var context = db.NewContext();
            context.UserSettings.Add(new Maki.Data.Identity.UserSetting
            {
                UserId = userId,
                Key = key,
                Value = value ?? string.Empty
            });
            context.SaveChanges();
            return Task.CompletedTask;
        }
    }

    private void SeedRead(int? seriesId, int chapters, DateTime at, int userId = UserId)
    {
        using var db = _db.NewContext();
        db.StatsEvents.Add(new StatsEvent
        {
            Type = StatsEventType.ChaptersRead,
            UserId = userId,
            SeriesId = seriesId,
            SeriesTitle = "Seeded",
            Timestamp = at,
            Value = chapters
        });
        db.SaveChanges();
    }

    private void SeedReadingTime(int? seriesId, int seconds, DateTime at, int userId = UserId)
    {
        using var db = _db.NewContext();
        db.StatsEvents.Add(new StatsEvent
        {
            Type = StatsEventType.ReadingTime,
            UserId = userId,
            SeriesId = seriesId,
            SeriesTitle = "Seeded",
            Timestamp = at,
            Value = seconds
        });
        db.SaveChanges();
    }

    // ---- The central guarantee -------------------------------------------------------------

    [Fact]
    public async Task FullyIncognitoReadingNeverReachesAnyCounter()
    {
        // StatsEventService drops a fully-incognito series before the row is written, so the log
        // simply has no events for it. This test states the consequence the feature depends on:
        // there is no incognito check anywhere in the metrics code, and there must not need to be.
        var incognito = _db.SeedSeries("Hidden", configure: s => s.Incognito = IncognitoMode.Full);
        using var db = _db.NewContext();
        var stats = new StatsEventService(db);
        stats.Record(StatsEventType.ChaptersRead, incognito, "Hidden", 500);
        await db.SaveChangesAsync();

        var metrics = await Metrics().GetAsync(UserId);

        Assert.Equal(0, metrics.ChaptersRead);
        Assert.Equal(0, metrics.DaysRead);
    }

    // ---- Evaluation -----------------------------------------------------------------------

    [Fact]
    public async Task EvaluatingTwiceUnlocksOnce()
    {
        SeedRead(null, 12, Now);

        using var db = _db.NewContext();
        var service = Achievements(db);

        var first = await service.EvaluateAsync(UserId);
        Assert.Contains(first, a => a.Key == "reader" && a.Tier == 1);
        Assert.Contains(first, a => a.Key == "first-page");

        var second = await service.EvaluateAsync(UserId);
        Assert.Empty(second);

        Assert.Equal(
            first.Count,
            _db.NewContext().UserAchievements.Count(a => a.UserId == UserId));
    }

    [Fact]
    public async Task EveryTierBelowTheOneEarnedIsRecorded()
    {
        // Somebody who first switches this on years in has genuinely passed the lower rungs, and a
        // grid showing Diamond with Bronze still locked would be nonsense.
        SeedRead(null, 3000, Now);

        using var db = _db.NewContext();
        await Achievements(db).EvaluateAsync(UserId);

        var tiers = _db.NewContext().UserAchievements
            .Where(a => a.UserId == UserId && a.Key == "reader")
            .Select(a => a.Tier)
            .OrderBy(t => t)
            .ToList();

        Assert.Equal([1, 2, 3, 4, 5], tiers);
    }

    [Fact]
    public async Task AnUnlockSurvivesTheMetricFallingBackBelowIt()
    {
        SeedRead(null, 60, Now);
        using (var db = _db.NewContext())
        {
            await Achievements(db).EvaluateAsync(UserId);
        }

        Assert.Equal(2, _db.NewContext().UserAchievements.Count(a => a.Key == "reader"));

        // The log is append-only in production; deleting here stands in for a series being removed
        // and taking its events' series link with it.
        using (var db = _db.NewContext())
        {
            db.StatsEvents.RemoveRange(db.StatsEvents);
            db.SaveChanges();
        }

        _cache.Remove($"metrics:{UserId}");
        using (var db = _db.NewContext())
        {
            Assert.Empty(await Achievements(db).EvaluateAsync(UserId));
        }

        Assert.Equal(2, _db.NewContext().UserAchievements.Count(a => a.Key == "reader"));
    }

    [Fact]
    public async Task NothingIsWrittenWhileTheFeatureIsOff()
    {
        await new TestUserSettingsStore(_db).SetAsync(
            UserId, SettingKeys.UserGamification,
            ProgressSpec.Serialize(new ProgressSpec(Enabled: false)));

        SeedRead(null, 500, Now);

        using var db = _db.NewContext();
        Assert.Empty(await Achievements(db).EvaluateAsync(UserId));
        Assert.Empty(_db.NewContext().UserAchievements.ToList());
    }

    [Fact]
    public async Task AcknowledgingOneTierSilencesTheWholeAchievement()
    {
        // Crossing several rungs at once is the normal case, and the UI collapses them into one
        // toast. If only the acknowledged row were stamped, the next page load would announce the
        // same achievement again one tier down, and again the load after that.
        SeedRead(null, 3000, Now);

        using (var db = _db.NewContext())
        {
            await Achievements(db).EvaluateAsync(UserId);
        }

        var top = _db.NewContext().UserAchievements
            .Where(a => a.Key == "reader")
            .OrderByDescending(a => a.Tier)
            .First();

        using (var db = _db.NewContext())
        {
            await Achievements(db).MarkSeenAsync(UserId, [top.Id]);
        }

        Assert.Empty(_db.NewContext().UserAchievements
            .Where(a => a.Key == "reader" && a.SeenAt == null)
            .ToList());

        // Only that achievement, though — an unrelated one must still be waiting to be shown.
        Assert.NotEmpty(_db.NewContext().UserAchievements
            .Where(a => a.Key != "reader" && a.SeenAt == null)
            .ToList());
    }

    // ---- Per-user isolation ----------------------------------------------------------------

    [Fact]
    public async Task OneUsersReadingNeverMovesAnothersLevel()
    {
        var other = _db.SeedUser("second");
        SeedRead(null, 5000, Now, userId: other);

        var mine = await Metrics().GetAsync(UserId);
        var theirs = await Metrics().GetAsync(other);

        Assert.Equal(0, mine.ChaptersRead);
        Assert.Equal(5000, theirs.ChaptersRead);
    }

    [Fact]
    public async Task LibraryCountersAreSharedRatherThanAttributedToWhoeverIsSignedIn()
    {
        // ChapterDownloaded carries a null UserId by design: it describes the instance. Both users
        // must see the same figure, and neither must see it as their own reading.
        var other = _db.SeedUser("second");
        using (var db = _db.NewContext())
        {
            db.StatsEvents.Add(new StatsEvent
            {
                Type = StatsEventType.ChapterDownloaded,
                UserId = null,
                SeriesTitle = "Seeded",
                Timestamp = Now,
                Value = 250
            });
            db.SaveChanges();
        }

        var mine = await Metrics().GetAsync(UserId);
        var theirs = await Metrics().GetAsync(other);

        Assert.Equal(250, mine.ChaptersDownloaded);
        Assert.Equal(250, theirs.ChaptersDownloaded);
        Assert.Equal(0, mine.ChaptersRead);
    }

    // ---- Days, streaks and goals -----------------------------------------------------------

    [Fact]
    public async Task DaysAreBucketedInTheUsersOwnTimeZone()
    {
        // 23:30 UTC on the 14th is already the 15th in Tokyo. Bucketing in UTC would put these two
        // reads on different days and break a streak that never lapsed.
        await new TestUserSettingsStore(_db).SetAsync(UserId, SettingKeys.UserTimeZone, "Asia/Tokyo");

        SeedRead(null, 1, new DateTime(2026, 6, 14, 23, 30, 0, DateTimeKind.Utc));
        SeedRead(null, 1, new DateTime(2026, 6, 15, 1, 0, 0, DateTimeKind.Utc));

        var metrics = await Metrics().GetAsync(UserId);
        Assert.Equal(1, metrics.DaysRead);
    }

    [Fact]
    public async Task AnUnknownTimeZoneFallsBackToUtcRatherThanThrowing()
    {
        await new TestUserSettingsStore(_db).SetAsync(
            UserId, SettingKeys.UserTimeZone, "Middle/Earth");

        var tz = await Metrics().TimeZoneForAsync(UserId);
        Assert.Equal(TimeZoneInfo.Utc, tz);
    }

    [Theory]
    // Three consecutive days ending today.
    [InlineData(new[] { -2, -1, 0 }, 3, 3)]
    // Ending yesterday still counts as current: the day is not over for the reader.
    [InlineData(new[] { -3, -2, -1 }, 3, 3)]
    // A two-day hole is a break, not a grace day.
    [InlineData(new[] { -10, -9, -3, -2, -1 }, 3, 3)]
    // One missed day is forgiven, and the skipped day counts toward the run.
    [InlineData(new[] { -4, -3, -1, 0 }, 5, 5)]
    // Ended a week ago: longest survives, current is zero.
    [InlineData(new[] { -9, -8, -7 }, 0, 3)]
    public void StreaksForgiveOneDayAWeekAndNeverPunishToday(int[] offsets, long current, long longest)
    {
        var today = new DateOnly(2026, 6, 15);
        var dates = offsets.Select(o => today.AddDays(o)).ToList();

        var (actualCurrent, actualLongest) = UserMetricsService.Streaks(dates, today);

        Assert.Equal(current, actualCurrent);
        Assert.Equal(longest, actualLongest);
    }

    [Fact]
    public void NoReadingIsNoStreak()
    {
        Assert.Equal((0, 0), UserMetricsService.Streaks([], new DateOnly(2026, 6, 15)));
    }

    [Theory]
    [InlineData(GoalPeriod.Day, 2026, 6, 15)]
    // 15 June 2026 is a Monday, so the week starts on it rather than on the Sunday before.
    [InlineData(GoalPeriod.Week, 2026, 6, 15)]
    [InlineData(GoalPeriod.Month, 2026, 6, 1)]
    [InlineData(GoalPeriod.Year, 2026, 1, 1)]
    public void GoalPeriodsStartWhereTheUsersCalendarSaysTheyDo(
        GoalPeriod period, int year, int month, int day)
    {
        var start = UserMetricsService.PeriodStart(new DateOnly(2026, 6, 15), period);
        Assert.Equal(new DateOnly(year, month, day), start);
    }

    [Fact]
    public void WeeksStartOnMondayEvenWhenTodayIsSunday()
    {
        // 21 June 2026 is a Sunday. A naive DayOfWeek subtraction would start the week on it.
        var start = UserMetricsService.PeriodStart(new DateOnly(2026, 6, 21), GoalPeriod.Week);
        Assert.Equal(new DateOnly(2026, 6, 15), start);
    }

    [Fact]
    public async Task ReadingTimeCountsTowardTheDayItWasSpentOn()
    {
        SeedReadingTime(null, 4 * 3600, Now);

        var metrics = await Metrics().GetAsync(UserId);

        Assert.Equal(4 * 3600, metrics.ReadingSeconds);
        Assert.Equal(4 * 3600, metrics.BestDaySeconds);
        Assert.Equal(1, metrics.DaysRead);
    }

    [Fact]
    public async Task PickingASeriesBackUpAfterThreeMonthsIsNoticed()
    {
        var seriesId = _db.SeedSeries();
        SeedRead(seriesId, 1, Now.AddDays(-200));
        SeedRead(seriesId, 1, Now.AddDays(-95));

        var metrics = await Metrics().GetAsync(UserId);
        Assert.True(metrics.ResumedAbandonedSeries);
    }

    [Fact]
    public async Task ReadingSteadilyIsNotAComeback()
    {
        var seriesId = _db.SeedSeries();
        SeedRead(seriesId, 1, Now.AddDays(-30));
        SeedRead(seriesId, 1, Now.AddDays(-20));

        var metrics = await Metrics().GetAsync(UserId);
        Assert.False(metrics.ResumedAbandonedSeries);
    }

    [Fact]
    public async Task GenresAndTypesComeFromTheSeriesActuallyRead()
    {
        var read = _db.SeedSeries("Read", configure: s =>
        {
            s.Genres = ["Action", "Drama"];
            s.Type = SeriesTypes.Manhwa;
        });
        _db.SeedSeries("Unread", configure: s =>
        {
            s.Genres = ["Comedy"];
            s.Type = SeriesTypes.Manga;
        });

        SeedRead(read, 3, Now);

        var metrics = await Metrics().GetAsync(UserId);

        Assert.Equal(2, metrics.DistinctGenres);
        Assert.Equal([SeriesTypes.Manhwa], metrics.TypesRead);
    }
}
