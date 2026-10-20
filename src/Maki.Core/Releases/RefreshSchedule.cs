using Maki.Core.Entities;

namespace Maki.Core.Releases;

/// <summary>Why a series is checked as often as it is. Sent to the client by name.</summary>
public enum RefreshTier
{
    /// <summary><c>monitoring.adaptiverefresh</c> is off: every series every 30 minutes.</summary>
    Fixed,

    /// <summary>No dated chapter and nothing seen arrive yet.</summary>
    Unknown,

    /// <summary>A chapter came out in the last few hours; fixes and the rest of a batch follow.</summary>
    JustReleased,

    Active,
    Slowing,
    Quiet,

    /// <summary>The source announced the next chapter, so the check lands just after it.</summary>
    Announced
}

public readonly record struct RefreshPlan(TimeSpan Interval, RefreshTier Tier);

/// <summary>
/// How long until a series is synced again, from how recently it last had a new chapter. Tiers
/// rather than a prediction: on measured data predicted release dates were right about half the
/// time, and a tier needs nothing but the newest chapter, so undated sources work too. A series
/// that speeds up again waits at most one tier interval before it is back on the fast one.
/// See docs/design-release-aware-polling.md.
/// </summary>
public static class RefreshSchedule
{
    public static readonly TimeSpan Floor = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan JustReleasedFor = TimeSpan.FromHours(6);
    public static readonly TimeSpan ActiveInterval = TimeSpan.FromHours(2);
    public static readonly TimeSpan ActiveFor = TimeSpan.FromDays(14);
    public static readonly TimeSpan SlowingInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan SlowingFor = TimeSpan.FromDays(60);
    public static readonly TimeSpan QuietInterval = TimeSpan.FromHours(24);
    public static readonly TimeSpan UnknownInterval = TimeSpan.FromHours(6);

    /// <summary>How long after an announced time the series is watched at the floor until the chapter shows up.</summary>
    public static readonly TimeSpan AnnouncedWatch = TimeSpan.FromHours(6);

    /// <summary>The API lists a chapter a moment after its announced time, not on the second.</summary>
    public static readonly TimeSpan AnnouncedSlack = TimeSpan.FromMinutes(5);

    /// <param name="lastNewChapter">
    /// The newest chapter's time: when Maki first saw one arrive, or a source's trustworthy release
    /// date. Null when there is neither.
    /// </param>
    /// <param name="announcedNext">The earliest next release any enabled source announced.</param>
    public static RefreshPlan Plan(
        DateTime now, DateTime? lastNewChapter, SeriesStatus status, DateTime? announcedNext, bool adaptive)
    {
        if (!adaptive)
        {
            return new RefreshPlan(Floor, RefreshTier.Fixed);
        }

        var plan = ByActivity(now, lastNewChapter, status);
        if (announcedNext is not { } next)
        {
            return plan;
        }

        if (next <= now)
        {
            var unseen = lastNewChapter is null || lastNewChapter < next;
            return unseen && now - next < AnnouncedWatch ? new RefreshPlan(Floor, RefreshTier.Announced) : plan;
        }

        var wait = next - now + AnnouncedSlack;
        return wait < plan.Interval ? new RefreshPlan(wait, RefreshTier.Announced) : plan;
    }

    private static RefreshPlan ByActivity(DateTime now, DateTime? lastNewChapter, SeriesStatus status)
    {
        if (lastNewChapter is not { } last)
        {
            return new RefreshPlan(UnknownInterval, RefreshTier.Unknown);
        }

        var age = now - last;
        if (age < JustReleasedFor)
        {
            return new RefreshPlan(Floor, RefreshTier.JustReleased);
        }

        if (age <= ActiveFor)
        {
            return new RefreshPlan(ActiveInterval, RefreshTier.Active);
        }

        // Activity wins over status: a "completed" series whose translation is still catching up
        // stays on the fast tiers above while chapters keep coming.
        if (status is SeriesStatus.Completed or SeriesStatus.Hiatus or SeriesStatus.Cancelled || age > SlowingFor)
        {
            return new RefreshPlan(QuietInterval, RefreshTier.Quiet);
        }

        return new RefreshPlan(SlowingInterval, RefreshTier.Slowing);
    }
}
