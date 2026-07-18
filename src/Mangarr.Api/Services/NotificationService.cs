using Mangarr.Core.Entities;
using Mangarr.Core.Notifications;
using Mangarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Services;

/// <summary>
/// Fans a <see cref="NotificationMessage"/> out to every enabled connection that has the
/// matching event toggle on. Per-connection failures are swallowed and logged — a broken
/// webhook must never fail a download or a job. Hot paths use <see cref="Dispatch"/>
/// (fire-and-forget, detached from the caller's scope and cancellation).
/// </summary>
public class NotificationService(
    IServiceScopeFactory scopeFactory,
    IEnumerable<INotificationProvider> providers,
    ILogger<NotificationService> logger)
{
    private readonly Dictionary<NotificationType, INotificationProvider> _providers =
        providers.ToDictionary(p => p.Type);

    /// <summary>Fire-and-forget dispatch for hot paths (download loop, jobs).</summary>
    public void Dispatch(NotificationEventType type, NotificationMessage message)
    {
        _ = Task.Run(() => DispatchAsync(type, message, CancellationToken.None));
    }

    public async Task DispatchAsync(NotificationEventType type, NotificationMessage message, CancellationToken ct = default)
    {
        List<Notification> targets;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MangarrDbContext>();
            targets = await db.Notifications
                .Where(n => n.Enabled)
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load notification connections for {Event}", type);
            return;
        }

        foreach (var connection in targets.Where(c => WantsEvent(c, type)))
        {
            try
            {
                await SendToAsync(connection, message, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Notification '{Name}' ({Type}) failed for {Event}",
                    connection.Name, connection.Type, type);
            }
        }
    }

    /// <summary>Sends to a single connection; throws on failure (used by the Test endpoint).</summary>
    public async Task SendToAsync(Notification connection, NotificationMessage message, CancellationToken ct = default)
    {
        if (!_providers.TryGetValue(connection.Type, out var provider))
        {
            throw new InvalidOperationException($"No provider for notification type {connection.Type}");
        }

        await provider.SendAsync(connection, message, ct);
    }

    private static bool WantsEvent(Notification c, NotificationEventType type) => type switch
    {
        NotificationEventType.ChapterDownloaded => c.OnChapterDownloaded,
        NotificationEventType.DownloadFailed => c.OnDownloadFailed,
        NotificationEventType.NewChapterAvailable => c.OnNewChapterAvailable,
        NotificationEventType.ImportCompleted => c.OnImportCompleted,
        NotificationEventType.HealthIssue => c.OnHealthIssue,
        NotificationEventType.Test => true,
        _ => false
    };
}
