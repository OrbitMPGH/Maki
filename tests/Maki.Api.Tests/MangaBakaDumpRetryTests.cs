using System.Collections.Specialized;
using Maki.Api.Jobs;
using Quartz;
using Quartz.Impl;

namespace Maki.Api.Tests;

public class MangaBakaDumpRetryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private IScheduler _scheduler = null!;

    public async Task InitializeAsync()
    {
        // Never started, so nothing fires: the tests only read back what was scheduled.
        _scheduler = await new StdSchedulerFactory(new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = "dump-retry-" + Guid.NewGuid().ToString("N"),
        }).GetScheduler();
        await _scheduler.AddJob(
            JobBuilder.Create<MangaBakaDumpRefreshJob>().WithIdentity(MangaBakaDumpRefreshJob.Key).StoreDurably().Build(),
            replace: true);
    }

    public Task DisposeAsync() => _scheduler.Shutdown();

    private Task ScheduleRegularAt(DateTimeOffset at) => _scheduler.ScheduleJob(TriggerBuilder.Create()
        .WithIdentity(MangaBakaDumpRetry.ScheduleTriggerKey)
        .ForJob(MangaBakaDumpRefreshJob.Key)
        .StartAt(at)
        .WithSimpleSchedule(s => s.WithIntervalInHours(6).RepeatForever())
        .Build());

    private async Task<DateTimeOffset?> RetryAt() =>
        (await _scheduler.GetTrigger(MangaBakaDumpRetry.RetryTriggerKey))?.GetNextFireTimeUtc();

    [Theory]
    [InlineData(1, 15)]
    [InlineData(2, 30)]
    [InlineData(3, 60)]
    [InlineData(5, 240)]
    [InlineData(6, 360)]
    [InlineData(40, 360)]
    public void The_delay_doubles_up_to_the_six_hour_schedule(int failures, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), MangaBakaDumpRetry.DelayAfter(failures));

    [Fact]
    public async Task A_failed_first_download_retries_sooner_each_time_backing_off()
    {
        await ScheduleRegularAt(Now.AddHours(6));
        var retry = new MangaBakaDumpRetry();

        Assert.Equal(Now.AddMinutes(15), await retry.OnFailureAsync(_scheduler, dumpUnusable: true, Now, default));
        Assert.Equal(Now.AddMinutes(15), await RetryAt());

        Assert.Equal(Now.AddMinutes(30), await retry.OnFailureAsync(_scheduler, dumpUnusable: true, Now, default));
        Assert.Equal(Now.AddMinutes(30), await RetryAt());
    }

    [Fact]
    public async Task No_retry_is_added_when_the_schedule_runs_first()
    {
        await ScheduleRegularAt(Now.AddMinutes(10));

        Assert.Null(await new MangaBakaDumpRetry().OnFailureAsync(_scheduler, dumpUnusable: true, Now, default));
        Assert.Null(await RetryAt());
    }

    [Fact]
    public async Task A_failed_refresh_of_an_installed_dump_keeps_the_schedule()
    {
        await ScheduleRegularAt(Now.AddHours(6));

        Assert.Null(await new MangaBakaDumpRetry().OnFailureAsync(_scheduler, dumpUnusable: false, Now, default));
        Assert.Null(await RetryAt());
    }

    [Fact]
    public async Task Success_resets_the_backoff_and_drops_the_pending_retry()
    {
        await ScheduleRegularAt(Now.AddHours(6));
        var retry = new MangaBakaDumpRetry();
        await retry.OnFailureAsync(_scheduler, dumpUnusable: true, Now, default);
        await retry.OnFailureAsync(_scheduler, dumpUnusable: true, Now, default);

        await retry.OnSuccessAsync(_scheduler, default);

        Assert.Null(await RetryAt());
        Assert.Equal(Now.AddMinutes(15), await retry.OnFailureAsync(_scheduler, dumpUnusable: true, Now, default));
    }
}
