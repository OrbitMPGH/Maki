using Quartz;

namespace Maki.Api.Services;

/// <summary>
/// Cancels every running Quartz job the moment the host starts shutting down.
/// <para>
/// Without this, Ctrl+C never stops a long job. <c>WaitForJobsToComplete = true</c> is deliberate —
/// an in-flight download should finish rather than be torn in half — but Quartz only signals a job's
/// <see cref="IJobExecutionContext.CancellationToken"/> from <see cref="IScheduler.Interrupt(JobKey, CancellationToken)"/>,
/// and a graceful shutdown does not interrupt anything. So the token stays unsignalled, the job keeps
/// walking the whole library issuing HTTP requests, and Quartz's own wait-for-jobs loop ignores the
/// host's shutdown-timeout token while it waits. The result is a process that logs "Application is
/// shutting down..." and then sits there for as long as the job takes.
/// </para>
/// <para>
/// Hooked to <see cref="IHostApplicationLifetime.ApplicationStopping"/> rather than to this service's
/// own <c>StopAsync</c> ordering: the host fires that token before it stops <em>any</em> hosted
/// service, so this works no matter where the registration lands relative to Quartz's.
/// </para>
/// </summary>
public class QuartzShutdownInterrupter(
    ISchedulerFactory schedulerFactory,
    IHostApplicationLifetime lifetime,
    ILogger<QuartzShutdownInterrupter> logger) : IHostedService
{
    // Written by the ApplicationStopping callback, read by StopAsync. The host runs those
    // sequentially (StopApplication, then the hosted services in reverse), so there is no race.
    private Task _interrupting = Task.CompletedTask;

    public Task StartAsync(CancellationToken ct)
    {
        lifetime.ApplicationStopping.Register(() => _interrupting = InterruptAllAsync());
        return Task.CompletedTask;
    }

    /// <summary>Lets the host's own shutdown wait cover the interrupt round-trip.</summary>
    public Task StopAsync(CancellationToken ct) => _interrupting;

    private async Task InterruptAllAsync()
    {
        try
        {
            // CancellationToken.None throughout: this *is* the shutdown path, and cancelling the
            // cancellation would leave the jobs running — exactly the state we are here to end.
            var scheduler = await schedulerFactory.GetScheduler(CancellationToken.None);
            var running = await scheduler.GetCurrentlyExecutingJobs(CancellationToken.None);

            foreach (var job in running)
            {
                logger.LogInformation("Shutdown: cancelling running job {Job}", job.JobDetail.Key);
                await scheduler.Interrupt(job.JobDetail.Key, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            // Never let this fault the shutdown path; a job that ignores its token is a slow exit,
            // an exception here would be a stuck one.
            logger.LogWarning(ex, "Failed to cancel running jobs during shutdown");
        }
    }
}
