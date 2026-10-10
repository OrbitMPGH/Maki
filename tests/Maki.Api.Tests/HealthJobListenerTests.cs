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
    public async Task A_run_cancelled_by_shutdown_leaves_the_recorded_state_alone()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var context = new TestJobContext(ct: cts.Token, detail: _detail);

        await Listener().JobWasExecuted(context, null);

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
    public async Task A_repeat_run_with_the_same_status_does_not_restamp_the_row_until_it_is_stale()
    {
        var listener = Listener();
        await listener.JobWasExecuted(new TestJobContext(detail: _detail), null);

        DateTime Stamp()
        {
            using var db = _fixture.NewContext();
            return db.HealthChecks.Single(c => c.Id == $"job:{_detail.Key}").CheckedAt;
        }

        var first = Stamp();
        await listener.JobWasExecuted(new TestJobContext(detail: _detail), null);
        Assert.Equal(first, Stamp());

        using (var db = _fixture.NewContext())
        {
            var row = db.HealthChecks.Single(c => c.Id == $"job:{_detail.Key}");
            row.CheckedAt = DateTime.UtcNow.AddMinutes(-6);
            db.SaveChanges();
        }

        await listener.JobWasExecuted(new TestJobContext(detail: _detail), null);
        Assert.True(Stamp() > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task A_clean_run_is_healthy()
    {
        await Listener().JobWasExecuted(new TestJobContext(detail: _detail), null);

        Assert.Equal("healthy", Status());
    }
}
