using Maki.Core.Entities;
using Maki.Core.Releases;

namespace Maki.Core.Tests;

public class RefreshScheduleTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private static RefreshPlan Plan(
        TimeSpan? sinceLastChapter, SeriesStatus status = SeriesStatus.Ongoing, DateTime? announced = null, bool adaptive = true) =>
        RefreshSchedule.Plan(Now, sinceLastChapter is { } age ? Now - age : null, status, announced, adaptive);

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    public static TheoryData<TimeSpan, RefreshTier, TimeSpan> TierEdges => new()
    {
        { TimeSpan.Zero, RefreshTier.JustReleased, RefreshSchedule.Floor },
        { RefreshSchedule.JustReleasedFor - Minute, RefreshTier.JustReleased, RefreshSchedule.Floor },
        { RefreshSchedule.JustReleasedFor, RefreshTier.Active, RefreshSchedule.ActiveInterval },
        { RefreshSchedule.ActiveFor, RefreshTier.Active, RefreshSchedule.ActiveInterval },
        { RefreshSchedule.ActiveFor + Minute, RefreshTier.Slowing, RefreshSchedule.SlowingInterval },
        { RefreshSchedule.SlowingFor, RefreshTier.Slowing, RefreshSchedule.SlowingInterval },
        { RefreshSchedule.SlowingFor + Minute, RefreshTier.Quiet, RefreshSchedule.QuietInterval },
    };

    [Theory]
    [MemberData(nameof(TierEdges))]
    public void The_tier_follows_the_age_of_the_newest_chapter(TimeSpan age, RefreshTier tier, TimeSpan interval)
    {
        var plan = Plan(age);

        Assert.Equal(tier, plan.Tier);
        Assert.Equal(interval, plan.Interval);
    }

    [Fact]
    public void An_older_newest_chapter_never_means_a_shorter_wait()
    {
        var intervals = Enumerable.Range(0, 120).Select(d => Plan(TimeSpan.FromDays(d)).Interval).ToList();

        Assert.Equal(intervals.Order(), intervals);
        Assert.True(intervals[0] < intervals[^1]);
    }

    [Fact]
    public void No_history_gets_the_unknown_tier()
    {
        Assert.Equal(new RefreshPlan(RefreshSchedule.UnknownInterval, RefreshTier.Unknown), Plan(null));
    }

    [Theory]
    [InlineData(SeriesStatus.Completed)]
    [InlineData(SeriesStatus.Hiatus)]
    [InlineData(SeriesStatus.Cancelled)]
    public void A_finished_or_paused_series_drops_to_daily_once_two_weeks_pass(SeriesStatus status)
    {
        Assert.Equal(RefreshTier.Quiet, Plan(RefreshSchedule.ActiveFor + TimeSpan.FromDays(1), status).Tier);
    }

    [Fact]
    public void Recent_chapters_outrank_a_completed_status()
    {
        Assert.Equal(RefreshTier.Active, Plan(RefreshSchedule.ActiveFor / 2, SeriesStatus.Completed).Tier);
    }

    [Fact]
    public void Off_means_every_thirty_minutes_whatever_the_history()
    {
        Assert.Equal(new RefreshPlan(RefreshSchedule.Floor, RefreshTier.Fixed), Plan(RefreshSchedule.SlowingFor * 6, adaptive: false));
    }

    [Fact]
    public void An_announcement_sooner_than_the_tier_pulls_the_check_to_just_after_it()
    {
        var until = RefreshSchedule.SlowingInterval / 2;
        var plan = Plan(RefreshSchedule.ActiveFor + TimeSpan.FromDays(1), announced: Now + until);

        Assert.Equal(RefreshTier.Announced, plan.Tier);
        Assert.Equal(until + RefreshSchedule.AnnouncedSlack, plan.Interval);
    }

    [Fact]
    public void An_announcement_later_than_the_tier_changes_nothing()
    {
        Assert.Equal(RefreshTier.Active, Plan(TimeSpan.FromDays(3), announced: Now + RefreshSchedule.ActiveInterval * 2).Tier);
    }

    [Fact]
    public void A_passed_announcement_is_watched_at_the_floor_until_the_chapter_shows()
    {
        var plan = Plan(TimeSpan.FromDays(7), announced: Now - RefreshSchedule.AnnouncedWatch / 2);

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
        Assert.Equal(RefreshTier.Active, Plan(TimeSpan.FromDays(7), announced: Now - RefreshSchedule.AnnouncedWatch - Minute).Tier);
    }
}
