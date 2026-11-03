using Maki.Api.Services;

namespace Maki.Api.Tests;

public class HealthWriteProbeTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_directory_that_passed_is_not_probed_again_until_the_interval_ends()
    {
        var probe = new HealthWriteProbe();
        Assert.True(probe.WriteDue("/library", Now));

        probe.RecordWrite("/library", true, Now);

        Assert.False(probe.WriteDue("/library", Now.AddMinutes(15)));
        Assert.False(probe.WriteDue("/library", Now + HealthWriteProbe.WriteInterval - TimeSpan.FromSeconds(1)));
        Assert.True(probe.WriteDue("/library", Now + HealthWriteProbe.WriteInterval));
        Assert.True(probe.WriteDue("/other", Now.AddMinutes(15)));
    }

    [Fact]
    public void A_failed_directory_is_probed_on_every_tick()
    {
        var probe = new HealthWriteProbe();
        probe.RecordWrite("/library", true, Now);
        probe.RecordWrite("/library", false, Now.AddMinutes(15));

        Assert.True(probe.WriteDue("/library", Now.AddMinutes(30)));
    }

    [Fact]
    public void Expire_forces_the_next_probe()
    {
        var probe = new HealthWriteProbe();
        probe.RecordWrite("/library", true, Now);
        probe.RecordStaging(1, "0:0:/library", 0, Now);

        probe.Expire();

        Assert.True(probe.WriteDue("/library", Now.AddMinutes(15)));
        Assert.Null(probe.CachedStaging(1, "0:0:/library", false, Now.AddMinutes(15)));
    }

    [Fact]
    public void A_clock_moved_back_probes_again()
    {
        var probe = new HealthWriteProbe();
        probe.RecordWrite("/library", true, Now);

        Assert.True(probe.WriteDue("/library", Now.AddHours(-1)));
    }

    [Fact]
    public void Staging_is_measured_again_while_an_operation_runs_or_after_the_operations_change()
    {
        var probe = new HealthWriteProbe();
        Assert.Null(probe.CachedStaging(1, "2:0:/library", false, Now));

        probe.RecordStaging(1, "2:0:/library", 4096, Now);

        Assert.Equal(4096, probe.CachedStaging(1, "2:0:/library", false, Now.AddMinutes(15)));
        Assert.Null(probe.CachedStaging(1, "2:0:/library", true, Now.AddMinutes(15)));
        Assert.Null(probe.CachedStaging(1, "3:1:/library", false, Now.AddMinutes(15)));
        Assert.Null(probe.CachedStaging(1, "2:0:/library", false, Now + HealthWriteProbe.StagingInterval));
        Assert.Null(probe.CachedStaging(2, "2:0:/library", false, Now.AddMinutes(15)));
    }

    [Fact]
    public void An_unreadable_staging_folder_is_not_remembered()
    {
        var probe = new HealthWriteProbe();
        probe.RecordStaging(1, "0:0:/library", 10, Now);
        probe.RecordStaging(1, "0:0:/library", null, Now.AddMinutes(15));

        Assert.Null(probe.CachedStaging(1, "0:0:/library", false, Now.AddMinutes(30)));
    }
}
