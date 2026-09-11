using Maki.Api.Services;
using Maki.Core.Entities;
using System.Text.Json;
namespace Maki.Api.Tests;
public class HealthTransitionTests
{
    [Fact]
    public void Connectivity_requires_two_failures_and_persisted_state_survives_restart()
    {
        var row = new HealthCheckRecord();
        Assert.False(HealthTransitions.Observe(row, "error", true, DateTime.UtcNow));
        Assert.True(HealthTransitions.Observe(row, "error", true, DateTime.UtcNow));
        var restored = JsonSerializer.Deserialize<HealthCheckRecord>(JsonSerializer.Serialize(row))!;
        Assert.False(HealthTransitions.Observe(restored, "error", true, DateTime.UtcNow));
        Assert.True(HealthTransitions.Observe(restored, "healthy", true, DateTime.UtcNow));
        Assert.False(HealthTransitions.Observe(restored, "healthy", true, DateTime.UtcNow));
    }
    [Fact]
    public void Transient_failure_has_no_recovery_notification()
    {
        var row = new HealthCheckRecord();
        Assert.False(HealthTransitions.Observe(row, "error", true, DateTime.UtcNow));
        Assert.False(HealthTransitions.Observe(row, "healthy", true, DateTime.UtcNow));
        Assert.False(HealthTransitions.Observe(row, "error", true, DateTime.UtcNow));
    }
    [Fact]
    public void Acknowledging_takes_a_check_off_the_header_badge()
    {
        var wants = HealthTransitions.Unattended.Compile();
        Assert.True(wants(new HealthCheckRecord { Status = "warning" }));
        Assert.False(wants(new HealthCheckRecord { Status = "warning", Acknowledged = true }));
        Assert.False(wants(new HealthCheckRecord { Status = "healthy" }));
    }
    [Fact]
    public void Escalation_reopens_acknowledgement_without_repeating_standing_issue()
    {
        var row = new HealthCheckRecord();
        Assert.True(HealthTransitions.Observe(row, "warning", false, DateTime.UtcNow));
        row.Acknowledged = true;
        Assert.False(HealthTransitions.Observe(row, "warning", false, DateTime.UtcNow));
        Assert.True(row.Acknowledged);
        Assert.True(HealthTransitions.Observe(row, "error", false, DateTime.UtcNow));
        Assert.False(row.Acknowledged);
    }
}
