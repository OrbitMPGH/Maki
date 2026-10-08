using Quartz;

namespace Maki.Api.Services;

/// <summary>A job that caught its own failure and says so, for <see cref="HealthJobListener"/>.</summary>
public sealed record JobFailure(string Message);

public static class JobOutcome
{
    /// <summary>
    /// Marks the run as failed without rethrowing. Most jobs swallow their exceptions so a failure
    /// logs once instead of surfacing as a Quartz job error; this is how the health page still
    /// learns of it.
    /// </summary>
    public static void ReportFailure(this IJobExecutionContext context, Exception ex) =>
        context.Result = new JobFailure(ex.Message);

    /// <summary>
    /// True when the run threw only because the job was interrupted, which is what shutdown does to
    /// any job that does not catch its own cancellation.
    /// </summary>
    public static bool WasInterrupted(IJobExecutionContext context, JobExecutionException? jobException) =>
        jobException?.GetBaseException() is OperationCanceledException && context.CancellationToken.IsCancellationRequested;

    public static bool Failed(IJobExecutionContext context, JobExecutionException? jobException) =>
        jobException != null || context.Result is JobFailure;
}
