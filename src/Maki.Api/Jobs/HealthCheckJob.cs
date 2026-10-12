using Maki.Api.Services;
using Quartz;
namespace Maki.Api.Jobs;
[DisallowConcurrentExecution]
public class HealthCheckJob(HealthMonitor monitor) : IJob
{
    public static readonly JobKey Key = new("health-check");

    private static long _lastRequested = long.MinValue;

    public Task Execute(IJobExecutionContext context) => monitor.RefreshAsync(context.CancellationToken);

    /// <summary>
    /// Runs the check now instead of at its next quarter hour, at most once a minute however many
    /// callers ask: a library import against a Suwayomi that is down fails one search per series.
    /// Never throws; a missed trigger only means the scheduled run reports it.
    /// </summary>
    public static async Task RequestSoonAsync(ISchedulerFactory schedulerFactory, ILogger logger)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastRequested);
        if ((last != long.MinValue && now - last < 60_000) ||
            Interlocked.CompareExchange(ref _lastRequested, now, last) != last)
        {
            return;
        }

        try
        {
            var scheduler = await schedulerFactory.GetScheduler();
            await scheduler.TriggerJob(Key);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not trigger an early health check");
        }
    }
}
