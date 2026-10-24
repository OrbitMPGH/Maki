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
    public async Task A_failed_run_schedules_a_retry_that_reschedules_itself_when_it_fails_too()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "maki-dump-retry-" + Guid.NewGuid().ToString("N")));
        try
        {
            var http = new FailingDumpHttp();
            var status = new MangaBakaDumpStatus();
            var dumpService = new MangaBakaDumpService(
                http, new MangaBakaDumpOptions(Path.Combine(dir.FullName, "mangabaka.db"), dir.FullName),
                new FakeAppSettings(), status, NullLogger<MangaBakaDumpService>.Instance);
            var job = new MangaBakaDumpRefreshJob(
                dumpService, status, new EventBroadcaster(null!, null!), schedulerFactory: null!,
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
        Assert.Equal(Now.AddMinutes(15), await retry.OnFailureAsync(_scheduler, dumpUnusable: true, Now, default));
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
