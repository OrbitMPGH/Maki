using Maki.Api.Services;

namespace Maki.Api.Tests;

public class DownloadIdleReleaseTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void An_instance_that_downloaded_nothing_never_collects()
    {
        var release = new DownloadIdleRelease();
        Assert.False(release.Due(0, busy: true, T0));
        Assert.False(release.Due(0, busy: false, T0));
        Assert.False(release.Due(0, busy: false, T0.AddMinutes(30)));
    }

    [Fact]
    public void A_burst_collects_once_after_the_queue_stays_idle()
    {
        var release = new DownloadIdleRelease();
        Assert.False(release.Due(3, busy: true, T0));
        Assert.False(release.Due(3, busy: true, T0.AddMinutes(5)));
        Assert.False(release.Due(3, busy: false, T0.AddMinutes(6)));
        Assert.False(release.Due(3, busy: false, T0.AddMinutes(7)));
        Assert.True(release.Due(3, busy: false, T0.AddMinutes(8)));
        Assert.False(release.Due(3, busy: false, T0.AddMinutes(20)));
    }

    [Fact]
    public void A_short_gap_between_items_does_not_collect()
    {
        var release = new DownloadIdleRelease();
        Assert.False(release.Due(1, busy: false, T0));
        Assert.False(release.Due(1, busy: false, T0.AddSeconds(90)));
        Assert.False(release.Due(2, busy: false, T0.AddSeconds(100)));
        Assert.False(release.Due(2, busy: false, T0.AddSeconds(150)));
        Assert.False(release.Due(2, busy: false, T0.AddSeconds(200)));
        Assert.True(release.Due(2, busy: false, T0.AddSeconds(280)));
    }

    [Fact]
    public void Work_in_flight_holds_it_back()
    {
        var release = new DownloadIdleRelease();
        Assert.False(release.Due(1, busy: false, T0));
        Assert.False(release.Due(1, busy: false, T0.AddMinutes(1)));
        Assert.False(release.Due(1, busy: true, T0.AddMinutes(3)));
        Assert.False(release.Due(1, busy: false, T0.AddMinutes(4)));
        Assert.True(release.Due(1, busy: false, T0.AddMinutes(6)));
    }
}
