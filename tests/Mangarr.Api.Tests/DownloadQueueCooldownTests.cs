using Mangarr.Api.Services;

namespace Mangarr.Api.Tests;

/// <summary>
/// Covers the shared scraper backoff. The service only touches the DB when enqueuing, so these
/// drive the cooldown directly with a hand-wound clock and no scope factory.
/// </summary>
public class DownloadQueueCooldownTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class StoppedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static (DownloadQueueService Queue, StoppedClock Clock) Build()
    {
        var clock = new StoppedClock(T0);
        return (new DownloadQueueService(null!, clock), clock);
    }

    [Fact]
    public void First_Hit_Backs_Off_Thirty_Seconds()
    {
        var (queue, _) = Build();

        var until = queue.EnterRateLimitCooldown(null);

        Assert.Equal(T0.UtcDateTime.AddSeconds(30), until);
        Assert.Equal(TimeSpan.FromSeconds(30), queue.CooldownRemaining());
    }

    [Fact]
    public void Consecutive_Incidents_Escalate()
    {
        var (queue, clock) = Build();

        queue.EnterRateLimitCooldown(null);

        // A new incident only counts once the previous cooldown has already elapsed.
        clock.Now = T0.AddSeconds(31);
        var until = queue.EnterRateLimitCooldown(null);

        Assert.Equal(clock.Now.UtcDateTime.AddSeconds(60), until);
    }

    [Fact]
    public void Hit_During_Cooldown_Does_Not_Extend_Or_Escalate()
    {
        var (queue, clock) = Build();

        var first = queue.EnterRateLimitCooldown(null);

        // An in-flight download reports its own 429 five seconds in: same incident.
        clock.Now = T0.AddSeconds(5);
        var second = queue.EnterRateLimitCooldown(null);
        Assert.Equal(first, second);

        // ...and it must not have advanced the escalation, so the next real incident is still 60s.
        clock.Now = T0.AddSeconds(31);
        var third = queue.EnterRateLimitCooldown(null);
        Assert.Equal(clock.Now.UtcDateTime.AddSeconds(60), third);
    }

    [Fact]
    public void RetryAfter_Is_Honored_Even_During_A_Cooldown()
    {
        var (queue, clock) = Build();

        queue.EnterRateLimitCooldown(null);

        clock.Now = T0.AddSeconds(5);
        var until = queue.EnterRateLimitCooldown(TimeSpan.FromMinutes(3));

        Assert.Equal(clock.Now.UtcDateTime.AddMinutes(3), until);
    }

    [Fact]
    public void RetryAfter_Is_Capped_At_Fifteen_Minutes()
    {
        var (queue, _) = Build();

        var until = queue.EnterRateLimitCooldown(TimeSpan.FromHours(2));

        Assert.Equal(T0.UtcDateTime.AddMinutes(15), until);
    }

    [Fact]
    public void Shorter_RetryAfter_Never_Shortens_An_Active_Cooldown()
    {
        var (queue, _) = Build();

        var long_ = queue.EnterRateLimitCooldown(TimeSpan.FromMinutes(10));
        var short_ = queue.EnterRateLimitCooldown(TimeSpan.FromMinutes(1));

        Assert.Equal(long_, short_);
    }

    [Fact]
    public void Backoff_Escalation_Resets_After_A_Success()
    {
        var (queue, clock) = Build();

        queue.EnterRateLimitCooldown(null);
        clock.Now = T0.AddSeconds(31);
        queue.EnterRateLimitCooldown(null); // 60s — second incident

        queue.ClearRateLimitBackoff();

        clock.Now = T0.AddMinutes(5);
        var until = queue.EnterRateLimitCooldown(null);

        Assert.Equal(clock.Now.UtcDateTime.AddSeconds(30), until);
    }

    [Fact]
    public void Escalation_Doubles_Then_Holds_At_The_Fifteen_Minute_Ceiling()
    {
        var (queue, clock) = Build();

        var durations = new List<TimeSpan>();
        for (var i = 0; i < 8; i++)
        {
            var now = clock.Now.UtcDateTime;
            var until = queue.EnterRateLimitCooldown(null);
            durations.Add(until - now);

            // Let each cooldown lapse so the next hit counts as a fresh incident.
            clock.Now = new DateTimeOffset(until, TimeSpan.Zero).AddSeconds(1);
        }

        Assert.Equal(
            [
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(2),
                TimeSpan.FromMinutes(4),
                TimeSpan.FromMinutes(8),
                TimeSpan.FromMinutes(15), // 16m, clamped
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(15)
            ],
            durations);
    }

    [Fact]
    public void CooldownRemaining_Is_Zero_Once_The_Window_Passes()
    {
        var (queue, clock) = Build();

        queue.EnterRateLimitCooldown(null);
        clock.Now = T0.AddSeconds(30);

        Assert.Equal(TimeSpan.Zero, queue.CooldownRemaining());
    }
}
