using Maki.Api.Services;

namespace Maki.Api.Tests;

public class HealthReclaimStatusTests
{
    private const long GiB = 1L << 30;

    [Fact]
    public void Shortfall_is_what_lifts_the_drive_past_both_warning_thresholds()
    {
        var options = new HealthOptions(WarningPercent: 10, WarningGiB: 10);
        Assert.Equal(4 * GiB, HealthMonitor.Shortfall(6 * GiB, 50 * GiB, options));
        Assert.Equal(20 * GiB, HealthMonitor.Shortfall(30 * GiB, 500 * GiB, options));
        Assert.Equal(0, HealthMonitor.Shortfall(60 * GiB, 500 * GiB, options));
    }

    [Fact]
    public void A_bin_too_small_to_clear_the_low_drive_stays_healthy()
    {
        var shortfalls = new Dictionary<string, long> { ["D:\\"] = 4 * GiB };
        Assert.Equal("healthy", HealthMonitor.ReclaimStatus(new Dictionary<string, long> { ["D:\\"] = GiB / 2 }, shortfalls));
    }

    [Fact]
    public void A_bin_that_clears_the_low_drive_warns()
    {
        var shortfalls = new Dictionary<string, long> { ["D:\\"] = 4 * GiB };
        Assert.Equal("warning", HealthMonitor.ReclaimStatus(new Dictionary<string, long> { ["D:\\"] = 5 * GiB }, shortfalls));
    }

    [Fact]
    public void A_bin_on_another_drive_does_not_count()
    {
        var shortfalls = new Dictionary<string, long> { ["D:\\"] = 4 * GiB };
        Assert.Equal("healthy", HealthMonitor.ReclaimStatus(new Dictionary<string, long> { ["E:\\"] = 50 * GiB }, shortfalls));
    }
}
