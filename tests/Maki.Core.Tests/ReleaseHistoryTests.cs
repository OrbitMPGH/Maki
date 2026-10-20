using Maki.Core.Releases;

namespace Maki.Core.Tests;

public class ReleaseHistoryTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Each_chapter_takes_its_earliest_trustworthy_time()
    {
        List<ChapterSighting> sightings =
        [
            new(1, Now.AddDays(-10), Reliable: true, FirstSeenAt: null),
            new(1, Now.AddDays(-12), Reliable: false, FirstSeenAt: Now.AddDays(-9)),
            new(2, Now.AddDays(-3), Reliable: false, FirstSeenAt: Now.AddDays(-2)),
            new(3, Now.AddDays(-1), Reliable: false, FirstSeenAt: null)
        ];

        Assert.Equal([Now.AddDays(-10), Now.AddDays(-2)], ReleaseHistory.ReleaseTimes(sightings, Now));
    }

    [Fact]
    public void The_newest_chapter_prefers_trusted_times()
    {
        List<ChapterSighting> sightings =
        [
            new(1, Now.AddDays(-40), Reliable: true, FirstSeenAt: null),
            new(2, Now.AddHours(-2), Reliable: false, FirstSeenAt: null)
        ];

        Assert.Equal(Now.AddDays(-40), ReleaseHistory.LastNewChapter(sightings, Now));
    }

    [Fact]
    public void Untrusted_dates_still_say_how_quiet_a_series_is_when_nothing_else_does()
    {
        List<ChapterSighting> sightings = [new(1, Now.AddDays(-90), Reliable: false, FirstSeenAt: null)];

        Assert.Equal(Now.AddDays(-90), ReleaseHistory.LastNewChapter(sightings, Now));
        Assert.Null(ReleaseHistory.LastNewChapter([], Now));
    }

    [Fact]
    public void A_chapter_dated_slightly_ahead_counts_as_now()
    {
        List<ChapterSighting> sightings = [new(1, Now.AddHours(5), Reliable: true, FirstSeenAt: null)];

        Assert.Equal(Now, ReleaseHistory.LastNewChapter(sightings, Now));
    }
}
