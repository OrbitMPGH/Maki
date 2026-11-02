using Microsoft.Extensions.Logging.Abstractions;
using Maki.Api.Services;

namespace Maki.Api.Tests;

/// <summary>
/// Covers the per-tracker scraper backoff. The service only touches the DB when enqueuing, so these
/// drive the cooldown directly with a hand-wound clock and no scope factory.
/// </summary>
public class DownloadQueueCooldownTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Source = "fake";

    private sealed class StoppedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (DownloadQueueService Queue, StoppedClock Clock) Build()
    {
        var clock = new StoppedClock(T0);
        return (new DownloadQueueService(null!, clock, null!, NullLogger<DownloadQueueService>.Instance), clock);
    }

    /// <summary>Hits the source once, then lets that cooldown lapse so the next hit is a fresh incident.</summary>
    private static TimeSpan Incident(DownloadQueueService queue, StoppedClock clock)
    {
        var now = clock.Now.UtcDateTime;
        var until = queue.EnterRateLimitCooldown(Source, null);
        clock.Now = new DateTimeOffset(until, TimeSpan.Zero).AddSeconds(1);
        return until - now;
    }

    [Fact]
    public void First_Hit_Starts_A_Cooldown_Below_The_Ceiling()
    {
        var (queue, _) = Build();

        var until = queue.EnterRateLimitCooldown(Source, null);

        var wait = until - T0.UtcDateTime;
        Assert.True(wait > TimeSpan.Zero);
        Assert.True(wait < DownloadQueueService.MaxCooldown);
        Assert.Equal(wait, queue.CooldownRemaining(Source));
    }

    [Fact]
    public void Consecutive_Incidents_Escalate()
    {
        var (queue, clock) = Build();

        var first = Incident(queue, clock);
        var second = Incident(queue, clock);

        Assert.True(second > first);
    }

    [Fact]
    public void Hit_During_Cooldown_Does_Not_Extend_Or_Escalate()
    {
        var (control, controlClock) = Build();
        Incident(control, controlClock);
        var secondIncident = Incident(control, controlClock);

        var (queue, clock) = Build();
        var first = queue.EnterRateLimitCooldown(Source, null);

        // An in-flight download reports its own 429 five seconds in: same incident.
        clock.Now = T0.AddSeconds(5);
        var second = queue.EnterRateLimitCooldown(Source, null);
        Assert.Equal(first, second);

        // ...and it must not have advanced the escalation: the next real incident is the second one.
        clock.Now = new DateTimeOffset(first, TimeSpan.Zero).AddSeconds(1);
        var third = queue.EnterRateLimitCooldown(Source, null);
        Assert.Equal(clock.Now.UtcDateTime + secondIncident, third);
    }

    [Fact]
    public void RetryAfter_Is_Honored_Even_During_A_Cooldown()
    {
        var (queue, clock) = Build();

        queue.EnterRateLimitCooldown(Source, null);

        clock.Now = T0.AddSeconds(5);
        var until = queue.EnterRateLimitCooldown(Source, TimeSpan.FromMinutes(3));

        Assert.Equal(clock.Now.UtcDateTime.AddMinutes(3), until);
    }

    [Fact]
    public void RetryAfter_Is_Capped_At_The_Ceiling()
    {
        var (queue, _) = Build();

        var until = queue.EnterRateLimitCooldown(Source, DownloadQueueService.MaxCooldown + TimeSpan.FromHours(1));

        Assert.Equal(T0.UtcDateTime + DownloadQueueService.MaxCooldown, until);
    }

    [Fact]
    public void Shorter_RetryAfter_Never_Shortens_An_Active_Cooldown()
    {
        var (queue, _) = Build();

        var long_ = queue.EnterRateLimitCooldown(Source, TimeSpan.FromMinutes(10));
        var short_ = queue.EnterRateLimitCooldown(Source, TimeSpan.FromMinutes(1));

        Assert.Equal(long_, short_);
    }

    [Fact]
    public void Backoff_Escalation_Resets_After_A_Success()
    {
        var (queue, clock) = Build();

        var first = Incident(queue, clock);
        Incident(queue, clock);
        Assert.True(Incident(queue, clock) > first);

        queue.ClearRateLimitBackoff(Source);

        Assert.Equal(first, Incident(queue, clock));
    }

    [Fact]
    public void Escalation_Grows_Then_Holds_At_The_Ceiling()
    {
        var (queue, clock) = Build();

        var waits = Enumerable.Range(0, 12).Select(_ => Incident(queue, clock)).ToList();

        Assert.All(waits, w => Assert.True(w > TimeSpan.Zero && w <= DownloadQueueService.MaxCooldown));
        for (var i = 1; i < waits.Count; i++)
        {
            Assert.True(waits[i] >= waits[i - 1], $"wait {i} shrank");
            if (waits[i - 1] < DownloadQueueService.MaxCooldown)
            {
                Assert.True(waits[i] > waits[i - 1], $"wait {i} did not grow below the ceiling");
            }
        }

        Assert.Equal(DownloadQueueService.MaxCooldown, waits[^1]);
        Assert.True(waits[0] < DownloadQueueService.MaxCooldown);
    }

    [Fact]
    public void CooldownRemaining_Is_Zero_Once_The_Window_Passes()
    {
        var (queue, clock) = Build();

        var until = queue.EnterRateLimitCooldown(Source, null);
        clock.Now = new DateTimeOffset(until, TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, queue.CooldownRemaining(Source));
    }

    [Fact]
    public void Cooldowns_Are_Independent_Per_Source()
    {
        var (queue, _) = Build();

        queue.EnterRateLimitCooldown("a", null);

        Assert.True(queue.CooldownRemaining("a") > TimeSpan.Zero);
        Assert.Equal(TimeSpan.Zero, queue.CooldownRemaining("b"));
    }
}
