using Maki.Api.Localization;
using System.Text.Json;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using Quartz;
using Quartz.Listener;
using Maki.Core.Inbox;
using Maki.Core.Notifications;
namespace Maki.Api.Services;
public class HealthJobListener(IServiceScopeFactory scopes, ILogger<HealthJobListener> logger) : JobListenerSupport
{
    private static readonly TimeSpan RestampAfter = TimeSpan.FromMinutes(5);

    public override string Name => "health-job-history";
    public override async Task JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken cancellationToken = default)
    {
        try
        {
            if (JobOutcome.WasInterrupted(context)) return;
            var failed = JobOutcome.Failed(context, jobException);
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            var id = $"job:{context.JobDetail.Key}";
            var row = await db.HealthChecks.FindAsync([id], cancellationToken);
            if (row == null) { row = new HealthCheckRecord { Id = id, Category = "job" }; db.HealthChecks.Add(row); }
            var status = failed ? "error" : "healthy";
            var previous = row.Status;
            var now = DateTime.UtcNow;
            // The 15 second poll would otherwise commit a write per run to restamp the same row.
            if (previous == status && now - row.CheckedAt < RestampAfter) return;
            var notify = HealthTransitions.Observe(row, status, false, now);
            row.MessageKey = failed ? "health.check.jobFailed" : "health.check.jobSucceeded";
            row.ParamsJson = JsonSerializer.Serialize(new { job = context.JobDetail.Key.ToString() });
            row.Message = string.Empty;
            // Some jobs run every 15 seconds; a row per run buried the history in thousands of pages.
            if (previous != status && !(previous == "unchecked" && status == "healthy"))
                db.HealthHistory.Add(new()
                {
                    Kind = "job",
                    MessageKey = row.MessageKey,
                    ParamsJson = row.ParamsJson,
                });
            await db.SaveChangesAsync(cancellationToken);
            if (notify)
            {
                var job = context.JobDetail.Key.ToString();
                var recovered = !failed;
                scope.ServiceProvider.GetRequiredService<InboxService>().Raise(
                    InboxEventType.HealthIssue,
                    new InboxMessage(
                        Key: recovered ? "inbox.job.recovered" : "inbox.job.failed",
                        Params: InboxMessage.Args(new { job }),
                        Url: "/health"),
                    InboxAudience.Admins);

                // Outbound to a chat channel, which has no language of its own to consult.
                var localizer = scope.ServiceProvider.GetRequiredService<IMessageCatalog>();
                var locale = await scope.ServiceProvider.GetRequiredService<IUserLocaleResolver>()
                    .DefaultAsync(cancellationToken);
                scope.ServiceProvider.GetRequiredService<NotificationService>().Dispatch(
                    NotificationEventType.HealthIssue,
                    new(NotificationEventType.HealthIssue,
                        localizer.GetFor(locale, recovered ? "notify.job.recovered.title" : "notify.job.failed.title"),
                        localizer.GetFor(locale, recovered ? "notify.job.recovered.body" : "notify.job.failed.body",
                            new { job })));
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Could not record job health"); }
    }
}

