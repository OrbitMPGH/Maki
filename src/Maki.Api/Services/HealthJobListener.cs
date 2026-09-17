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
    public override string Name => "health-job-history";
    public override async Task JobWasExecuted(IJobExecutionContext context, JobExecutionException? jobException, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            var id = $"job:{context.JobDetail.Key}";
            var row = await db.HealthChecks.FindAsync([id], cancellationToken);
            if (row == null) { row = new HealthCheckRecord { Id = id, Category = "job" }; db.HealthChecks.Add(row); }
            var status = jobException == null ? "healthy" : "error";
            var notify = HealthTransitions.Observe(row, status, false, DateTime.UtcNow);
            row.MessageKey = jobException == null ? "health.check.jobSucceeded" : "health.check.jobFailed";
            row.ParamsJson = JsonSerializer.Serialize(new { job = context.JobDetail.Key.ToString() });
            row.Message = string.Empty;
            db.HealthHistory.Add(new()
            {
                Kind = "job",
                MessageKey = row.MessageKey,
                ParamsJson = row.ParamsJson,
            });
            await db.SaveChangesAsync(cancellationToken);
            if (notify)
            {
                var title = jobException == null ? "Background job recovered" : "Background job failed";
                scope.ServiceProvider.GetRequiredService<InboxService>().Raise(InboxEventType.HealthIssue, InboxMessage.Unkeyed(title, row.Message, url: "/health"), InboxAudience.Admins);
                scope.ServiceProvider.GetRequiredService<NotificationService>().Dispatch(NotificationEventType.HealthIssue, new(NotificationEventType.HealthIssue, title, row.Message));
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Could not record job health"); }
    }
}

