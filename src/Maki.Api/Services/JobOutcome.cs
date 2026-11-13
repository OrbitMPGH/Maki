using System.Collections.Concurrent;
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

    // Fire instance ids an admin stopped from the Health page, until the listener sees the run end.
    private static readonly ConcurrentDictionary<string, byte> Stopped = new();

    /// <summary>
    /// True when the job's token was cancelled: by <see cref="QuartzShutdownInterrupter"/>, or by an
    /// admin's Stop (<see cref="StoppedByAdmin"/> tells the two apart). Quartz swallows the resulting
    /// cancellation, so the run reaches the listener looking like a clean one. Whatever it reports says
    /// nothing about the job.
    /// </summary>
    public static bool WasInterrupted(IJobExecutionContext context) =>
        context.CancellationToken.IsCancellationRequested;

    /// <summary>Records that an admin is about to interrupt this run, before the interrupt is sent.</summary>
    public static void MarkStopped(string fireInstanceId) => Stopped[fireInstanceId] = 0;

    /// <summary>Takes back a mark whose interrupt found no run.</summary>
    public static void Unmark(string fireInstanceId) => Stopped.TryRemove(fireInstanceId, out _);

    /// <summary>
    /// True when this run was cancelled by an admin rather than by shutdown. A job that polls for a daily
    /// slot counts such a run as the day's, or it would start again on its next tick.
    /// </summary>
    public static bool StoppedByAdmin(IJobExecutionContext context) =>
        context.CancellationToken.IsCancellationRequested && Stopped.ContainsKey(context.FireInstanceId);

    /// <summary>Drops this run's mark once it has ended.</summary>
    public static void Forget(IJobExecutionContext context) => Stopped.TryRemove(context.FireInstanceId, out _);

    public static bool Failed(IJobExecutionContext context, JobExecutionException? jobException) =>
        jobException != null || context.Result is JobFailure;
}
