using Maki.Core.Entities;
using Maki.Core.Releases;

namespace Maki.Core.Tests;

public class RefreshScheduleTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private static RefreshPlan Plan(
        TimeSpan? sinceLastChapter, SeriesStatus status = SeriesStatus.Ongoing, DateTime? announced = null, bool adaptive = true) =>
        RefreshSchedule.Plan(Now, sinceLastChapter is { } age ? Now - age : null, status, announced, adaptive);

    [Theory]
    [InlineData(1, RefreshTier.JustReleased, 0.5)]
    [InlineData(24 * 3, RefreshTier.Active, 2)]
    [InlineData(24 * 14, RefreshTier.Active, 2)]
    [InlineData(24 * 30, RefreshTier.Slowing, 6)]
    [InlineData(24 * 61, RefreshTier.Quiet, 24)]
    public void The_tier_follows_the_age_of_the_newest_chapter(int hours, RefreshTier tier, double intervalHours)
    {
        var plan = Plan(TimeSpan.FromHours(hours));

        Assert.Equal(tier, plan.Tier);
        Assert.Equal(TimeSpan.FromHours(intervalHours), plan.Interval);
    }

    [Fact]
    public void No_history_checks_every_six_hours()
    {
        Assert.Equal(new RefreshPlan(TimeSpan.FromHours(6), RefreshTier.Unknown), Plan(null));
    }

    [Theory]
    [InlineData(SeriesStatus.Completed)]
    [InlineData(SeriesStatus.Hiatus)]
    [InlineData(SeriesStatus.Cancelled)]
    public void A_finished_or_paused_series_drops_to_daily_once_two_weeks_pass(SeriesStatus status)
    {
        Assert.Equal(RefreshTier.Quiet, Plan(TimeSpan.FromDays(20), status).Tier);
    }

    [Fact]
    public void Recent_chapters_outrank_a_completed_status()
    {
        Assert.Equal(RefreshTier.Active, Plan(TimeSpan.FromDays(5), SeriesStatus.Completed).Tier);
    }

    [Fact]
    public void Off_means_every_thirty_minutes_whatever_the_history()
    {
        Assert.Equal(new RefreshPlan(RefreshSchedule.Floor, RefreshTier.Fixed), Plan(TimeSpan.FromDays(400), adaptive: false));
    }

    [Fact]
    public void An_announcement_sooner_than_the_tier_pulls_the_check_to_just_after_it()
    {
        var plan = Plan(TimeSpan.FromDays(30), announced: Now.AddHours(3));

        Assert.Equal(RefreshTier.Announced, plan.Tier);
        Assert.Equal(TimeSpan.FromHours(3) + RefreshSchedule.AnnouncedSlack, plan.Interval);
    }

    [Fact]
    public void An_announcement_later_than_the_tier_changes_nothing()
    {
        Assert.Equal(RefreshTier.Active, Plan(TimeSpan.FromDays(3), announced: Now.AddDays(4)).Tier);
    }

    [Fact]
    public void A_passed_announcement_is_watched_at_the_floor_until_the_chapter_shows()
    {
        var plan = Plan(TimeSpan.FromDays(7), announced: Now.AddHours(-1));

        Assert.Equal(new RefreshPlan(RefreshSchedule.Floor, RefreshTier.Announced), plan);
    }

    [Fact]
    public void A_passed_announcement_already_seen_is_left_alone()
    {
        Assert.Equal(RefreshTier.JustReleased, Plan(TimeSpan.FromMinutes(20), announced: Now.AddHours(-1)).Tier);
    }

    [Fact]
    public void A_long_passed_announcement_stops_holding_the_floor()
    {
        Assert.Equal(RefreshTier.Active, Plan(TimeSpan.FromDays(7), announced: Now.AddHours(-12)).Tier);
    }
}
