using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Data.Identity;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Tests;

/// <summary>
/// When <see cref="RatingCheckpointService"/> arms a "how is it so far?" prompt, and what answering
/// or dismissing one does.
/// </summary>
public sealed class RatingCheckpointTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private int SeedSeries(SeriesStatus status = SeriesStatus.Ongoing, int? total = null) =>
        _db.SeedSeries($"Series {Guid.NewGuid():N}", configure: s =>
        {
            s.Status = status;
            s.TotalChapters = total;
        });

    /// <summary>Chapters 1..<paramref name="chapters"/>, all wanted; the first <paramref name="read"/> completed.</summary>
    private void SeedReading(int userId, int seriesId, int chapters, int read, bool watched = false)
    {
        using var db = _db.NewContext();
        for (var n = 1; n <= chapters; n++)
        {
            var chapter = new Chapter { SeriesId = seriesId, Number = n, Wanted = true, FileRemovedAt = Now };
            db.Chapters.Add(chapter);
            db.SaveChanges();
            if (n <= read)
            {
                db.ChapterProgress.Add(new ChapterProgress
                {
                    UserId = userId, SeriesId = seriesId, ChapterId = chapter.Id, PageCount = 20,
                    Completed = true, Watched = watched, StartedAt = Now, UpdatedAt = Now,
                });
            }
        }

        db.SaveChanges();
    }

    private async Task<RatingCheckpoints> Arm(int userId, int seriesId, DateTime? at = null)
    {
        using var db = _db.NewContext(userId);
        var series = await db.Series.SingleAsync(s => s.Id == seriesId);
        await RatingCheckpointService.ArmAsync(db, userId, series, at ?? Now, default);
        return await db.UserSeriesStates.Where(s => s.SeriesId == seriesId)
            .Select(s => s.PendingRatingCheckpoint).FirstOrDefaultAsync();
    }

    private void SeedState(int userId, int seriesId, Action<UserSeriesState> configure)
    {
        using var db = _db.NewContext();
        var state = new UserSeriesState { UserId = userId, SeriesId = seriesId };
        configure(state);
        db.UserSeriesStates.Add(state);
        db.SaveChanges();
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData(200, 10)]
    [InlineData(40, 6)]
    [InlineData(8, 3)]
    public void Hooked_waits_for_ten_chapters_or_fifteen_percent(int? total, int expected) =>
        Assert.Equal(expected, RatingCheckpointService.HookedThreshold(total));

    [Fact]
    public async Task Ten_genuine_reads_arm_hooked()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries();
        SeedReading(alice, seriesId, chapters: 40, read: 10);

        Assert.Equal(RatingCheckpoints.Hooked, await Arm(alice, seriesId));
    }

    [Fact]
    public async Task Watched_chapters_do_not_count_toward_hooked()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries();
        SeedReading(alice, seriesId, chapters: 40, read: 12, watched: true);

        Assert.Equal(RatingCheckpoints.None, await Arm(alice, seriesId));
    }

    [Fact]
    public async Task Reading_the_last_wanted_chapter_of_an_ongoing_series_arms_caught_up()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries();
        SeedReading(alice, seriesId, chapters: 5, read: 5);

        Assert.Equal(RatingCheckpoints.CaughtUp, await Arm(alice, seriesId));
    }

    [Fact]
    public async Task A_completed_series_is_never_caught_up()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries(SeriesStatus.Completed, total: 5);
        SeedReading(alice, seriesId, chapters: 5, read: 5);
        SeedState(alice, seriesId, s => s.RatingCheckpointsSeen = RatingCheckpoints.Hooked);

        Assert.Equal(RatingCheckpoints.None, await Arm(alice, seriesId));
    }

    [Fact]
    public async Task A_final_rating_stops_every_prompt()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries();
        SeedReading(alice, seriesId, chapters: 20, read: 20);
        SeedState(alice, seriesId, s => s.Rating = 8);

        Assert.Equal(RatingCheckpoints.None, await Arm(alice, seriesId));
    }

    [Fact]
    public async Task A_fresh_so_far_score_holds_caught_up_back()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries();
        SeedReading(alice, seriesId, chapters: 20, read: 20);
        SeedState(alice, seriesId, s =>
        {
            s.ProvisionalRating = 7;
            s.ProvisionalRatedAt = Now.AddDays(-2);
            s.ProvisionalAtChapter = 18;
        });

        Assert.Equal(RatingCheckpoints.None, await Arm(alice, seriesId));
    }

    [Fact]
    public async Task Only_one_checkpoint_is_armed_per_day_across_the_library()
    {
        var alice = _db.SeedUser("alice");
        var first = SeedSeries();
        var second = SeedSeries();
        SeedReading(alice, first, chapters: 5, read: 5);
        SeedReading(alice, second, chapters: 5, read: 5);

        Assert.Equal(RatingCheckpoints.CaughtUp, await Arm(alice, first));
        Assert.Equal(RatingCheckpoints.None, await Arm(alice, second));
        // The cap defers rather than drops: the next completion on another day arms it.
        Assert.Equal(RatingCheckpoints.CaughtUp, await Arm(alice, second, Now.AddDays(1)));
    }

    [Fact]
    public async Task Three_dismissals_in_a_row_stop_asking_on_that_series()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries();
        SeedReading(alice, seriesId, chapters: 5, read: 5);
        SeedState(alice, seriesId, s => s.RatingPromptDismissals = RatingCheckpointService.MaxDismissals);

        Assert.Equal(RatingCheckpoints.None, await Arm(alice, seriesId));
    }

    [Fact]
    public async Task Switching_prompts_off_arms_nothing()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries();
        SeedReading(alice, seriesId, chapters: 5, read: 5);
        _db.SetUserConfig(alice, (SettingKeys.RatingPrompts, "off"));

        Assert.Equal(RatingCheckpoints.None, await Arm(alice, seriesId));
    }

    [Fact]
    public async Task Another_readers_checkpoint_does_not_use_up_the_daily_cap()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");
        var seriesId = SeedSeries();
        SeedReading(alice, seriesId, chapters: 5, read: 5);
        SeedReading(bob, seriesId, chapters: 0, read: 0);
        SeedState(bob, seriesId, s => s.LastRatingCheckpointAt = Now);

        Assert.Equal(RatingCheckpoints.CaughtUp, await Arm(alice, seriesId));
    }

    [Fact]
    public void Answering_marks_the_checkpoint_seen_and_resets_dismissals()
    {
        var state = new UserSeriesState { PendingRatingCheckpoint = RatingCheckpoints.Hooked, RatingPromptDismissals = 2 };

        RatingCheckpointService.Answered(state);

        Assert.Equal(RatingCheckpoints.None, state.PendingRatingCheckpoint);
        Assert.Equal(RatingCheckpoints.Hooked, state.RatingCheckpointsSeen);
        Assert.Equal(0, state.RatingPromptDismissals);
    }

    [Fact]
    public async Task Dismissing_through_the_endpoint_counts_toward_the_back_off()
    {
        var alice = _db.SeedUser("alice");
        var seriesId = SeedSeries();
        SeedState(alice, seriesId, s => s.PendingRatingCheckpoint = RatingCheckpoints.CaughtUp);

        using (var db = _db.NewContext(alice))
        {
            var controller = new SeriesController(new TestLocalizer(), db, null!, null!, null!, null!, null!, null!,
                null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!,
                new TestCurrentUser(alice), null!, null!, null!, null!);
            await controller.DismissRatingCheckpoint(seriesId, default);
        }

        using var check = _db.NewContext();
        var state = await check.UserSeriesStates.SingleAsync(s => s.UserId == alice && s.SeriesId == seriesId);
        Assert.Equal(RatingCheckpoints.None, state.PendingRatingCheckpoint);
        Assert.Equal(RatingCheckpoints.CaughtUp, state.RatingCheckpointsSeen);
        Assert.Equal(1, state.RatingPromptDismissals);
    }
}
