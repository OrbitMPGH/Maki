using Maki.Api.Hubs;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Writes in-app notifications and pushes them to whoever is connected.
/// <para>
/// Deliberately parallel to <see cref="NotificationService"/> rather than folded into it. That one
/// fans out to Discord and webhooks, which are instance-wide chat channels with no recipient: giving
/// them achievements, level-ups and per-user request outcomes would drown the download reports they
/// exist for. The two systems share raise <em>sites</em> and nothing else.
/// </para>
/// <para>
/// A singleton that opens its own scope per raise, because the callers include other singletons
/// (<see cref="DownloadBatchNotifier"/>), hosted services and Quartz jobs. That scope is unrestricted,
/// so every write names its user explicitly and the <c>IUserOwned</c> stamp never fires.
/// </para>
/// </summary>
public class InboxService(
    IServiceScopeFactory scopeFactory,
    InboxAudienceResolver audiences,
    IUserSettingsStore userSettings,
    EventBroadcaster events,
    TimeProvider time,
    ILogger<InboxService> logger)
{
    /// <summary>
    /// Fire-and-forget raise for hot paths (the download loop, jobs). Detached from the caller's
    /// scope and cancellation, and swallowing its own failures: a notification must never be the
    /// reason a download or a job fails. Virtual so tests can record without a database.
    /// </summary>
    public virtual void Raise(InboxEventType type, InboxMessage message, InboxAudience audience)
    {
        _ = Task.Run(() => RaiseAsync(type, message, audience, CancellationToken.None));
    }

    /// <summary>
    /// Fire-and-forget raise at whoever tracks a series, for callers that hold a series id but not
    /// its root folder. The lookup runs in this service's own unrestricted scope, the same place
    /// <c>EventBroadcaster.AudienceForAsync</c> resolves its audience, so it does not inherit the
    /// caller's filters. Callers that already have the root folder should pass
    /// <see cref="InboxAudience.SeriesTrackers"/> directly and save the query.
    /// </summary>
    public virtual void RaiseForSeries(InboxEventType type, InboxMessage message, int seriesId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
                var rootFolderId = await db.Series
                    .IgnoreQueryFilters()
                    .Where(s => s.Id == seriesId)
                    .Select(s => (int?)s.RootFolderId)
                    .FirstOrDefaultAsync(CancellationToken.None);

                if (rootFolderId is not { } folder)
                {
                    return;
                }

                await RaiseAsync(type, message, InboxAudience.SeriesTrackers(seriesId, folder));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not raise inbox notification {Event} for series {SeriesId}", type, seriesId);
            }
        });
    }

    public virtual async Task RaiseAsync(
        InboxEventType type, InboxMessage message, InboxAudience audience, CancellationToken ct = default)
    {
        if (type == InboxEventType.Unknown)
        {
            return;
        }

        // An admin-only event raised at a non-admin audience means a call site named the wrong rule.
        // Delivering it anyway would show a reader the shape of the instance's internals, so it is
        // caught here rather than trusted to every call site.
        if (InboxEventTypes.IsAdminOnly(type) && audience.Kind != InboxAudienceKind.Admins)
        {
            logger.LogWarning(
                "Inbox event {Event} is admin-only but was raised for {Audience}; dropped", type, audience.Kind);
            return;
        }

        try
        {
            var recipients = await audiences.ResolveAsync(audience, ct);
            var wanted = new List<int>(recipients.Count);
            foreach (var userId in recipients)
            {
                var prefs = InboxPrefsSpec.Parse(
                    await userSettings.GetAsync(userId, SettingKeys.NotificationsInbox, ct));

                if (prefs.Wants(type))
                {
                    wanted.Add(userId);
                }
            }

            if (wanted.Count == 0)
            {
                return;
            }

            var now = time.GetUtcNow().UtcDateTime;
            var rows = wanted.Select(userId => new UserNotification
            {
                UserId = userId,
                Type = type,
                Level = message.Level,
                Title = message.Title,
                Body = message.Body,
                SeriesId = message.SeriesId,
                ChapterId = message.ChapterId,
                Url = message.Url,
                CreatedAt = now,
            }).ToList();

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            db.UserNotifications.AddRange(rows);
            await db.SaveChangesAsync(ct);

            // Pushed after the save so the id is real and a client that reloads instead of patching
            // its cache sees the same row it was just told about. One grouped count rather than one
            // per recipient — a popular series can resolve to every account on the instance.
            var unreadByUser = await db.UserNotifications
                .IgnoreQueryFilters()
                .Where(n => wanted.Contains(n.UserId) && n.ReadAt == null)
                .GroupBy(n => n.UserId)
                .Select(g => new { UserId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.UserId, x => x.Count, ct);

            foreach (var row in rows)
            {
                var unread = unreadByUser.GetValueOrDefault(row.UserId, 1);
                await events.InboxNotification(row.UserId, InboxNotificationPush.From(row, unread));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not raise inbox notification {Event}", type);
        }
    }
}

/// <summary>
/// What a client receives over SignalR: the row it would have got from the REST feed, plus the
/// recipient's new unread count so the badge doesn't need a second round trip to update.
/// </summary>
public record InboxNotificationPush(
    int Id,
    string Type,
    string Level,
    string Title,
    string Body,
    int? SeriesId,
    int? ChapterId,
    string? Url,
    DateTime CreatedAt,
    int Unread)
{
    public static InboxNotificationPush From(UserNotification row, int unread) => new(
        row.Id,
        InboxEventTypes.Key(row.Type),
        row.Level.ToString().ToLowerInvariant(),
        row.Title,
        row.Body,
        row.SeriesId,
        row.ChapterId,
        row.Url,
        row.CreatedAt,
        unread);
}
