using Mangarr.Api.Services;
using Mangarr.Core.Notifications;
using Quartz;

namespace Mangarr.Api.Jobs;

/// <summary>
/// Periodically recomputes health issues and fires a <see cref="NotificationEventType.HealthIssue"/>
/// notification for each newly-appeared one. Diffing against the last check (via
/// <see cref="HealthState"/>) keeps it from re-notifying the same standing problem every tick.
/// </summary>
[DisallowConcurrentExecution]
public class HealthCheckJob(
    HealthCheckService healthCheck,
    HealthState state,
    NotificationService notifications,
    ILogger<HealthCheckJob> logger) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;

        var issues = await healthCheck.GetIssuesAsync(ct);
        var fresh = state.Diff(issues);

        foreach (var issue in fresh)
        {
            logger.LogInformation("New health issue ({Type}): {Message}", issue.Type, issue.Message);
            notifications.Dispatch(NotificationEventType.HealthIssue, new NotificationMessage(
                NotificationEventType.HealthIssue,
                Title: "Health issue",
                Body: issue.Message,
                Level: issue.Severity == "error" ? NotificationLevel.Error : NotificationLevel.Warning));
        }
    }
}
