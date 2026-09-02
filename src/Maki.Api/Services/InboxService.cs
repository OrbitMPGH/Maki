using Maki.Api.Hubs;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Security;
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
            if (recipients.Count == 0)
            {
                return;
            }

            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

            // One query for every recipient's prefs row rather than a scope+query per recipient
            // (recipients.Count can be every admin, or every tracker of a popular series).
            var prefsByUser = await db.UserSettings
                .AsNoTracking()
                .Where(s => recipients.Contains(s.UserId) && s.Key == SettingKeys.NotificationsInbox)
                .ToDictionaryAsync(s => s.UserId, s => s.Value, ct);

            var specByUser = recipients.ToDictionary(
                userId => userId,
                userId => InboxPrefsSpec.Parse(prefsByUser.GetValueOrDefault(userId)));

            var wanted = recipients.Where(userId => specByUser[userId].Wants(type)).ToList();

            if (wanted.Count == 0)
            {
                return;
            }

            // The per-series layer runs second on purpose: it only ever removes recipients, so a
            // type switched off globally cannot be switched back on by pinning one series to All.
            if (message.SeriesId is { } seriesId)
            {
                wanted = await FilterBySeriesModeAsync(db, type, wanted, seriesId, specByUser, ct);

                if (wanted.Count == 0)
                {
                    return;
                }
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

    /// <summary>
    /// Narrows recipients to the ones who want to hear about <em>this series</em>, per their
    /// <see cref="SeriesNotificationMode"/> for it, falling back to their global
    /// <c>InboxPrefsSpec.SeriesDefault</c> where they have expressed no opinion.
    /// <para>
    /// Keyed off <c>InboxMessage.SeriesId</c> rather than a list of event types, so a series-scoped
    /// event added later is covered without a second place to remember. The one carve-out is
    /// <c>InboxEventTypes.IsOperational</c>: an admin keeps receiving those however they set the
    /// series, since a muted series whose downloads are broken is still broken.
    /// </para>
    /// <para>
    /// Note what this does to <see cref="InboxAudienceKind.SeriesTrackers"/>' admin fallback: an
    /// admin who sets their default to Reading has no progress rows for a series nobody tracks, so
    /// they stop hearing about it. That is the setting working, not a hole in the audience rule.
    /// </para>
    /// </summary>
    private static async Task<List<int>> FilterBySeriesModeAsync(
        MakiDbContext db,
        InboxEventType type,
        List<int> recipients,
        int seriesId,
        IReadOnlyDictionary<int, InboxPrefsSpec> specByUser,
        CancellationToken ct)
    {
        // IgnoreQueryFilters throughout, same as every other read here: the scope is unrestricted
        // and each query names its users explicitly. Absent row = Default = follow the global.
        var modes = await db.UserSeriesStates
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.SeriesId == seriesId && recipients.Contains(s.UserId))
            .Select(s => new { s.UserId, s.NotificationMode })
            .ToDictionaryAsync(x => x.UserId, x => x.NotificationMode, ct);

        // GetValueOrDefault lands on Default (0) for a user with no row, which is the same answer
        // as an explicitly stored Default: consult their global setting.
        static SeriesNotificationMode Effective(SeriesNotificationMode mode, InboxPrefsSpec spec) =>
            mode == SeriesNotificationMode.Default ? spec.ResolvedSeriesDefault : mode;

        var effective = recipients.ToDictionary(
            userId => userId,
            userId => Effective(modes.GetValueOrDefault(userId), specByUser[userId]));

        // Only pay for the "who is reading this" queries when somebody's answer depends on them,
        // which on an instance nobody has reconfigured is never.
        var reading = effective.Values.Contains(SeriesNotificationMode.Reading)
            ? await ReadersOfAsync(db, recipients, seriesId, ct)
            : [];

        // A failed download still reaches the people who can fix it, whatever they set the series
        // to. Muting is a reading preference, not an ops switch.
        var exempt = InboxEventTypes.IsOperational(type)
            ? await AdminsAmongAsync(db, recipients, ct)
            : [];

        return recipients
            .Where(userId => exempt.Contains(userId) || effective[userId] switch
            {
                SeriesNotificationMode.Muted => false,
                SeriesNotificationMode.Reading => reading.Contains(userId),
                _ => true,
            })
            .ToList();
    }

    /// <summary>
    /// Which of <paramref name="recipients"/> hold the Admin bit. Tests the flag directly rather
    /// than through <c>MakiPermissions.Grants</c> so it translates to SQL, the same way
    /// <see cref="InboxAudienceResolver"/> does — Admin is the one flag where the bare bit test and
    /// the grant semantics agree. No usable-account filter: these ids already came back from the
    /// resolver, which applied it.
    /// </summary>
    private static async Task<HashSet<int>> AdminsAmongAsync(
        MakiDbContext db, List<int> recipients, CancellationToken ct) =>
        [.. await db.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => recipients.Contains(u.Id) && (u.Permissions & MakiPermission.Admin) != 0)
            .Select(u => u.Id)
            .ToListAsync(ct)];

    /// <summary>
    /// Which of <paramref name="recipients"/> is still reading the series: they have progress on it
    /// — from the built-in reader (<see cref="ChapterProgress"/>) or a Kavita high-water mark
    /// (<see cref="ReadingState"/>) — and have not marked it finished. Being caught up still counts;
    /// a reader waiting on the next chapter is exactly who the notification is for.
    /// </summary>
    private static async Task<HashSet<int>> ReadersOfAsync(
        MakiDbContext db, List<int> recipients, int seriesId, CancellationToken ct)
    {
        var progressed = await db.ChapterProgress
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.SeriesId == seriesId && recipients.Contains(p.UserId))
            .Select(p => p.UserId)
            .Distinct()
            .ToListAsync(ct);

        var states = await db.ReadingStates
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.SeriesId == seriesId && recipients.Contains(r.UserId))
            .Select(r => new { r.UserId, r.Finished })
            .ToListAsync(ct);

        var reading = progressed.Concat(states.Select(r => r.UserId)).ToHashSet();
        reading.ExceptWith(states.Where(r => r.Finished).Select(r => r.UserId));
        return reading;
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
