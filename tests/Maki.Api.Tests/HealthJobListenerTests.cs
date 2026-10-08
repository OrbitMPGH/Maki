using Maki.Api.Jobs;
using Maki.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Quartz;

namespace Maki.Api.Tests;

public class HealthJobListenerTests : IDisposable
{
    private readonly TestDb _fixture = new();
    private readonly IJobDetail _detail = JobBuilder.Create<HealthCheckJob>().WithIdentity("test-job").Build();

    public void Dispose() => _fixture.Dispose();

    private HealthJobListener Listener() =>
        new(_fixture.ScopeFactory(), NullLogger<HealthJobListener>.Instance);

    private string? Status()
    {
        using var db = _fixture.NewContext();
        return db.HealthChecks.SingleOrDefault(c => c.Id == $"job:{_detail.Key}")?.Status;
    }

    [Fact]
    public async Task A_run_cancelled_by_shutdown_is_not_recorded_as_a_failure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var context = new TestJobContext(ct: cts.Token, detail: _detail);

        await Listener().JobWasExecuted(context, new JobExecutionException(new OperationCanceledException()));

        Assert.Null(Status());
    }

    [Fact]
    public async Task A_job_that_threw_for_another_reason_is_a_failure()
    {
        var context = new TestJobContext(detail: _detail);

        await Listener().JobWasExecuted(context, new JobExecutionException(new InvalidOperationException("boom")));

        Assert.Equal("error", Status());
    }

    [Fact]
    public async Task A_job_that_caught_its_own_failure_and_reported_it_is_a_failure()
    {
        var context = new TestJobContext(detail: _detail);
        context.ReportFailure(new InvalidOperationException("dump download failed"));

        await Listener().JobWasExecuted(context, null);

        Assert.Equal("error", Status());
    }

    [Fact]
    public async Task A_clean_run_is_healthy()
    {
        await Listener().JobWasExecuted(new TestJobContext(detail: _detail), null);

        Assert.Equal("healthy", Status());
    }
}
