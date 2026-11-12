using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The per-user reading status: reads move a series to Reading wherever they are saved, the two auto
/// rules, and the On hold and Dropped series leaving Home's reading rail.
/// </summary>
public class ReadingStatusTests : IDisposable
{
    private const int User = 1;
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private sealed class Clock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    private int SeedChapter(int seriesId, decimal number, DateTime addedAt)
    {
        using var db = _db.NewContext();
        var file = new ChapterFile { SeriesId = seriesId, RelativePath = $"{seriesId}-{number}.cbz", DateAdded = addedAt };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        var chapter = new Chapter { SeriesId = seriesId, Number = number, ChapterFileId = file.Id };
        db.Chapters.Add(chapter);
        db.SaveChanges();
        return chapter.Id;
    }

    private void Read(int seriesId, int chapterId, DateTime at, bool watched = false)
    {
        using var db = _db.NewContext();
        db.ChapterProgress.Add(new ChapterProgress
        {
            UserId = User, SeriesId = seriesId, ChapterId = chapterId, Completed = true, Watched = watched,
            PageCount = 20, PageIndex = 19, StartedAt = at, UpdatedAt = at,
        });
        db.SaveChanges();
    }

    private void SetStatus(int seriesId, ReadingStatus status, bool auto = false, DateTime? changedAt = null)
    {
        using var db = _db.NewContext();
        var row = db.UserSeriesStates.FirstOrDefault(s => s.UserId == User && s.SeriesId == seriesId);
        if (row is null)
        {
            row = new UserSeriesState { UserId = User, SeriesId = seriesId };
            db.UserSeriesStates.Add(row);
        }

        row.Status = status;
        row.StatusAuto = auto;
        row.StatusChangedAt = changedAt ?? Now;
        row.UpdatedAt = Now;
        db.SaveChanges();
    }

    private UserSeriesState? State(int seriesId)
    {
        using var db = _db.NewContext();
        return db.UserSeriesStates.AsNoTracking().FirstOrDefault(s => s.UserId == User && s.SeriesId == seriesId);
    }

    // ---- reads move the series to Reading ----

    [Fact]
    public void A_first_read_creates_the_row_as_Reading()
    {
        var series = _db.SeedSeries("Berserk");
        Read(series, SeedChapter(series, 1, Now), Now);

        var state = State(series);
        Assert.NotNull(state);
        Assert.Equal(ReadingStatus.Reading, state.Status);
        Assert.False(state.StatusAuto);
    }

    [Fact]
    public void A_read_brings_a_dropped_series_back_to_Reading_and_keeps_the_rest_of_the_row()
    {
        var series = _db.SeedSeries("Berserk");
        SetStatus(series, ReadingStatus.Dropped);
        using (var db = _db.NewContext())
        {
            db.UserSeriesStates.Single(s => s.SeriesId == series).Rating = 8;
            db.SaveChanges();
        }

        Read(series, SeedChapter(series, 1, Now), Now);

        var state = State(series)!;
        Assert.Equal(ReadingStatus.Reading, state.Status);
        Assert.Equal(8, state.Rating);
    }

    [Fact]
    public void A_watched_tick_is_not_a_read()
    {
        var series = _db.SeedSeries("Berserk");
        Read(series, SeedChapter(series, 1, Now), Now, watched: true);

        Assert.Null(State(series));
    }

    [Fact]
    public void Re_saving_a_completed_chapter_or_marking_it_unread_leaves_the_status_alone()
    {
        var series = _db.SeedSeries("Berserk");
        var chapter = SeedChapter(series, 1, Now);
        Read(series, chapter, Now);
        SetStatus(series, ReadingStatus.Dropped);

        using (var db = _db.NewContext())
        {
            var row = db.ChapterProgress.Single(p => p.ChapterId == chapter);
            row.ReadSeconds = 30;
            db.SaveChanges();
            row.Completed = false;
            row.PageIndex = 0;
            row.UnreadAt = Now;
            db.SaveChanges();
        }

        Assert.Equal(ReadingStatus.Dropped, State(series)!.Status);
    }

    [Fact]
    public void Completing_a_watched_chapter_is_a_read()
    {
        var series = _db.SeedSeries("Berserk");
        var chapter = SeedChapter(series, 1, Now);
        Read(series, chapter, Now, watched: true);
        SetStatus(series, ReadingStatus.OnHold);

        using (var db = _db.NewContext())
        {
            db.ChapterProgress.Single(p => p.ChapterId == chapter).Watched = false;
            db.SaveChanges();
        }

        Assert.Equal(ReadingStatus.Reading, State(series)!.Status);
    }

    // ---- setting it ----

    [Fact]
    public async Task Setting_a_status_reports_only_the_series_that_changed()
    {
        var a = _db.SeedSeries("A");
        var b = _db.SeedSeries("B");
        SetStatus(a, ReadingStatus.Dropped);
        using var db = _db.NewContext();

        var changed = await new ReadingStatusService(db).SetAsync(User, [a, b], ReadingStatus.Dropped, auto: false, CancellationToken.None);

        Assert.Equal([b], changed);
        Assert.Equal(ReadingStatus.Dropped, State(b)!.Status);
    }

    [Fact]
    public async Task Setting_plan_to_read_on_a_series_with_no_row_writes_nothing()
    {
        var a = _db.SeedSeries("A");
        using var db = _db.NewContext();

        var changed = await new ReadingStatusService(db).SetAsync(User, [a], ReadingStatus.PlanToRead, auto: false, CancellationToken.None);

        Assert.Empty(changed);
        Assert.Null(State(a));
    }

    // ---- auto rules ----

    private (ReadingStatusAutoService Service, List<(ReadingStatus Status, int[] Ids)> Pushed) Rules()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.NewContext());
        services.AddScoped<ContinueReadingService>();
        services.AddScoped<ReadingStatusService>();
        var scopes = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var pushed = new List<(ReadingStatus, int[])>();
        var service = new ReadingStatusAutoService(scopes, null!, new Clock(Now), NullLogger<ReadingStatusAutoService>.Instance)
        {
            QueuePush = (_, series, status) => pushed.Add((status, [.. series.Select(s => s.Id)])),
        };
        return (service, pushed);
    }

    /// <summary>A Reading series with chapter 1 read at <paramref name="readAt"/> and chapter 2 waiting since <paramref name="waitingSince"/>.</summary>
    private int SeedIdle(string title, DateTime readAt, DateTime waitingSince)
    {
        var series = _db.SeedSeries(title);
        var first = SeedChapter(series, 1, readAt.AddDays(-1));
        SeedChapter(series, 2, waitingSince);
        Read(series, first, readAt);
        SetStatus(series, ReadingStatus.Reading, changedAt: readAt); // the read stamped the real clock, not Now
        return series;
    }

    [Fact]
    public async Task Hold_rule_pauses_a_series_left_unread_past_the_limit()
    {
        var idle = SeedIdle("Idle", Now.AddDays(-40), Now.AddDays(-50));
        var recent = SeedIdle("Recent", Now.AddDays(-10), Now.AddDays(-50));
        var (rules, pushed) = Rules();

        var (held, dropped) = await rules.RunForUserAsync(User, holdDays: 30, dropDays: null, CancellationToken.None);

        Assert.Equal([idle], held);
        Assert.Empty(dropped);
        var state = State(idle)!;
        Assert.Equal(ReadingStatus.OnHold, state.Status);
        Assert.True(state.StatusAuto);
        Assert.Equal(ReadingStatus.Reading, State(recent)!.Status);
        var push = Assert.Single(pushed);
        Assert.Equal(ReadingStatus.OnHold, push.Status);
        Assert.Equal([idle], push.Ids);
    }

    [Fact]
    public async Task A_caught_up_series_is_left_alone()
    {
        var series = _db.SeedSeries("Caught up");
        var only = SeedChapter(series, 1, Now.AddDays(-200));
        Read(series, only, Now.AddDays(-100));
        var (rules, _) = Rules();

        var (held, dropped) = await rules.RunForUserAsync(User, holdDays: 30, dropDays: 60, CancellationToken.None);

        Assert.Empty(held);
        Assert.Empty(dropped);
        Assert.Equal(ReadingStatus.Reading, State(series)!.Status);
    }

    [Fact]
    public async Task The_clock_starts_when_the_next_chapter_arrives()
    {
        // Caught up for months, then chapter 2 landed five days ago: not idle yet.
        var series = SeedIdle("Weekly", Now.AddDays(-120), Now.AddDays(-5));
        var (rules, _) = Rules();

        var (held, _) = await rules.RunForUserAsync(User, holdDays: 30, dropDays: null, CancellationToken.None);

        Assert.Empty(held);
    }

    [Fact]
    public async Task Drop_rule_moves_an_auto_hold_on_but_not_a_hand_set_one()
    {
        var autoHeld = SeedIdle("Auto", Now.AddDays(-100), Now.AddDays(-110));
        var handHeld = SeedIdle("Hand", Now.AddDays(-100), Now.AddDays(-110));
        SetStatus(autoHeld, ReadingStatus.OnHold, auto: true, changedAt: Now.AddDays(-70));
        SetStatus(handHeld, ReadingStatus.OnHold, auto: false, changedAt: Now.AddDays(-95));
        var (rules, _) = Rules();

        var (_, dropped) = await rules.RunForUserAsync(User, holdDays: 30, dropDays: 90, CancellationToken.None);

        Assert.Equal([autoHeld], dropped);
        Assert.Equal(ReadingStatus.Dropped, State(autoHeld)!.Status);
        Assert.Equal(ReadingStatus.OnHold, State(handHeld)!.Status);
    }

    [Fact]
    public async Task Setting_Reading_by_hand_restarts_the_clock()
    {
        var series = SeedIdle("Back on", Now.AddDays(-100), Now.AddDays(-110));
        SetStatus(series, ReadingStatus.Reading, changedAt: Now.AddDays(-3));
        var (rules, _) = Rules();

        var (held, dropped) = await rules.RunForUserAsync(User, holdDays: 30, dropDays: 90, CancellationToken.None);

        Assert.Empty(held);
        Assert.Empty(dropped);
    }

    // ---- Home's reading rail ----

    [Fact]
    public async Task On_hold_and_dropped_series_leave_the_reading_rail_until_the_next_read()
    {
        var reading = SeedIdle("Reading", Now.AddDays(-1), Now.AddDays(-2));
        var held = SeedIdle("Held", Now.AddDays(-1), Now.AddDays(-2));
        var dropped = SeedIdle("Dropped", Now.AddDays(-1), Now.AddDays(-2));
        SetStatus(held, ReadingStatus.OnHold);
        SetStatus(dropped, ReadingStatus.Dropped);

        Assert.Equal([reading], await RailAsync());

        SeedChapter(dropped, 3, Now);
        using (var db = _db.NewContext())
        {
            var chapter2 = db.Chapters.Single(c => c.SeriesId == dropped && c.Number == 2).Id;
            db.ChapterProgress.Add(new ChapterProgress
            {
                UserId = User, SeriesId = dropped, ChapterId = chapter2, PageIndex = 3, PageCount = 20,
                StartedAt = Now, UpdatedAt = Now,
            });
            db.SaveChanges();
        }

        Assert.Contains(dropped, await RailAsync());
        Assert.DoesNotContain(held, await RailAsync());
    }

    private async Task<List<int>> RailAsync()
    {
        var context = _db.NewContext(User);
        var paths = new Maki.Api.Configuration.AppPaths(Path.Combine(Path.GetTempPath(), "maki-tests", "reading-status-accents"));
        var covers = new CoverService(null!, paths, new FakeAppSettings(), NullLogger<CoverService>.Instance);
        var controller = new HomeController(context, new ContinueReadingService(context),
            new CoverAccentCache(covers, NullLogger<CoverAccentCache>.Instance));
        var response = Assert.IsType<HomeReadingResponse>(
            Assert.IsType<OkObjectResult>(await controller.Reading(ct: CancellationToken.None)).Value);
        return [.. response.ContinueReading.Select(i => i.SeriesId)];
    }
}
