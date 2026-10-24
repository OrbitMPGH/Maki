using Quartz;

namespace Maki.Api.Jobs;

/// <summary>
/// Retries a failed MangaBaka dump download sooner than the six-hour schedule while no dump is
/// installed, since most of the web app waits on it: 15 minutes, then 30, 60 and so on, until the
/// schedule's own next run comes first. A failed refresh of a dump already on disk keeps the
/// schedule. A singleton, so the count of failures in a row survives between job instances.
/// </summary>
public sealed class MangaBakaDumpRetry
{
    public static readonly TriggerKey RetryTriggerKey = new("mangabaka-dump-retry");
    public static readonly TriggerKey ScheduleTriggerKey = new("mangabaka-dump-trigger");

    public static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromHours(6);

    private int _failures;

    /// <summary>The wait after the <paramref name="failures"/>th failure in a row (1-based).</summary>
    public static TimeSpan DelayAfter(int failures)
    {
        var delay = FirstDelay;
        for (var i = 1; i < failures && delay < MaxDelay; i++)
        {
            delay *= 2;
        }

        return delay < MaxDelay ? delay : MaxDelay;
    }

    /// <summary>
    /// Schedules the next try when the dump is still missing, unless the regular trigger fires first.
    /// Returns when the retry will run, or null when none was scheduled.
    /// </summary>
    public async Task<DateTimeOffset?> OnFailureAsync(
        IScheduler scheduler, bool dumpMissing, DateTimeOffset now, CancellationToken ct)
    {
        if (!dumpMissing)
        {
            Interlocked.Exchange(ref _failures, 0);
            return null;
        }

        var at = now + DelayAfter(Interlocked.Increment(ref _failures));
        if ((await scheduler.GetTrigger(ScheduleTriggerKey, ct))?.GetNextFireTimeUtc() is { } next && next <= at)
        {
            return null;
        }

        var trigger = TriggerBuilder.Create()
            .WithIdentity(RetryTriggerKey)
            .ForJob(MangaBakaDumpRefreshJob.Key)
            .StartAt(at)
            .Build();

        // Quartz keeps a trigger rescheduled from inside the run it fired rather than deleting it.
        if (await scheduler.CheckExists(RetryTriggerKey, ct))
        {
            await scheduler.RescheduleJob(RetryTriggerKey, trigger, ct);
        }
        else
        {
            await scheduler.ScheduleJob(trigger, ct);
        }

        return at;
    }

    public async Task OnSuccessAsync(IScheduler scheduler, CancellationToken ct)
    {
        Interlocked.Exchange(ref _failures, 0);
        await scheduler.UnscheduleJob(RetryTriggerKey, ct);
    }
}
