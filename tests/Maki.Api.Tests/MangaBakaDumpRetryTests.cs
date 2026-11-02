using System.Collections.Specialized;
using System.Net;
using Maki.Api.Hubs;
using Maki.Api.Jobs;
using Maki.Metadata.MangaBaka;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;
using Quartz.Impl;
using Quartz.Spi;

namespace Maki.Api.Tests;

public class MangaBakaDumpRetryTests : IAsyncLifetime
{
    // Quartz weighs trigger times against the real clock, so "now" has to stay ahead of it: a fixed
    // date turns every test here into a failure once the wall clock passes it.
    private readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds());

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

    [Fact]
    public void The_delay_starts_at_the_first_delay_and_grows_until_it_reaches_the_schedule()
    {
        Assert.Equal(MangaBakaDumpRetry.FirstDelay, MangaBakaDumpRetry.DelayAfter(1));

        var delays = Enumerable.Range(1, 40).Select(n => MangaBakaDumpRetry.DelayAfter(n)).ToList();
        for (var i = 1; i < delays.Count; i++)
        {
            Assert.True(delays[i] >= delays[i - 1], $"delay {i + 1} shrank");
            if (delays[i - 1] < MangaBakaDumpRetry.MaxDelay)
            {
                Assert.True(delays[i] > delays[i - 1], $"delay {i + 1} did not grow below the cap");
            }
        }

        Assert.All(delays, d => Assert.True(d <= MangaBakaDumpRetry.MaxDelay));
        Assert.Equal(MangaBakaDumpRetry.MaxDelay, delays[^1]);
    }

    [Fact]
    public async Task A_failed_first_download_retries_sooner_each_time_backing_off()
    {
        await ScheduleRegularAt(Now.AddHours(6));
        var retry = new MangaBakaDumpRetry();

        var first = await retry.OnFailureAsync(_scheduler, dumpUnusable: true, Now, default);
        Assert.Equal(Now + MangaBakaDumpRetry.FirstDelay, first);
        Assert.Equal(first, await RetryAt());

        var second = await retry.OnFailureAsync(_scheduler, dumpUnusable: true, Now, default);
        Assert.True(second > first);
        Assert.Equal(second, await RetryAt());
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
    public async Task A_failed_run_schedules_a_retry_that_reschedules_itself_when_it_fails_too()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "maki-dump-retry-" + Guid.NewGuid().ToString("N")));
        try
        {
            var http = new FailingDumpHttp();
            var status = new MangaBakaDumpStatus();
            var dumpOptions = new MangaBakaDumpOptions(Path.Combine(dir.FullName, "mangabaka.db"), dir.FullName);
            var dumpService = new MangaBakaDumpService(
                http, dumpOptions, new FakeAppSettings(), status, NullLogger<MangaBakaDumpService>.Instance);
            var job = new MangaBakaDumpRefreshJob(
                dumpService, dumpOptions, status, new EventBroadcaster(null!, null!), schedulerFactory: null!,
                new ArtifactBuildGate(NullLogger<ArtifactBuildGate>.Instance),
                new MangaBakaDumpRetry(TimeSpan.FromMilliseconds(200)),
                NullLogger<MangaBakaDumpRefreshJob>.Instance);
            _scheduler.JobFactory = new OneJob(job);
            await _scheduler.Start();

            await _scheduler.TriggerJob(MangaBakaDumpRefreshJob.Key);

            // The manual run fails, its retry (+200 ms) fails, and that one schedules the next
            // (+400 ms), which is the third request.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (http.Calls < 3 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.True(http.Calls >= 3, $"only {http.Calls} attempts");
            Assert.NotNull(status.Snapshot().LastError);
        }
        finally
        {
            await _scheduler.Shutdown(waitForJobsToComplete: true);
            try { dir.Delete(recursive: true); } catch (IOException) { }
        }
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
        Assert.Equal(Now + MangaBakaDumpRetry.FirstDelay, await retry.OnFailureAsync(_scheduler, dumpUnusable: true, Now, default));
    }

    private sealed class OneJob(IJob job) : IJobFactory
    {
        public IJob NewJob(TriggerFiredBundle bundle, IScheduler scheduler) => job;

        public void ReturnJob(IJob job)
        {
        }
    }

    /// <summary>Every request fails, so every refresh does; counts the requests.</summary>
    private sealed class FailingDumpHttp : IHttpClientFactory
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public HttpClient CreateClient(string name) =>
            new(new Handler(this)) { BaseAddress = new Uri("https://fixture.test/") };

        private sealed class Handler(FailingDumpHttp owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref owner._calls);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }
        }
    }
}
