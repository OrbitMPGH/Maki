using Maki.Api.Localization;
using Maki.Core.Entities;
using Maki.Core.Notifications;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Fans a <see cref="NotificationMessage"/> out to every enabled connection that has the
/// matching event toggle on. Per-connection failures are swallowed and logged — a broken
/// webhook must never fail a download or a job. Hot paths use <see cref="Dispatch"/>
/// (fire-and-forget, detached from the caller's scope and cancellation).
/// </summary>
public class NotificationService(
    IServiceScopeFactory scopeFactory,
    IEnumerable<INotificationProvider> providers,
    ILogger<NotificationService> logger,
    IUserLocaleResolver? locales = null,
    IMessageCatalog? catalog = null)
{
    private readonly Dictionary<NotificationType, INotificationProvider> _providers =
        providers.ToDictionary(p => p.Type);

    /// <summary>Every registered provider's form schema, in enum order.</summary>
    public IReadOnlyList<NotificationProviderDescriptor> Descriptors =>
        _providers.Values.Select(p => p.Descriptor).OrderBy(d => d.Type).ToList();

    public NotificationProviderDescriptor? DescriptorFor(NotificationType type) =>
        _providers.TryGetValue(type, out var provider) ? provider.Descriptor : null;

    /// <summary>The provider's cross-field check, as a catalogue key; null when consistent or the type is unknown.</summary>
    public string? ValidateConfig(NotificationType type, NotificationFields fields) =>
        _providers.TryGetValue(type, out var provider) ? provider.Validate(fields) : null;

    /// <summary>Fire-and-forget dispatch for hot paths (download loop, jobs). Virtual so tests can record.</summary>
    public virtual void Dispatch(NotificationEventType type, NotificationMessage message)
    {
        _ = Task.Run(() => DispatchAsync(type, message, CancellationToken.None));
    }

    public async Task DispatchAsync(NotificationEventType type, NotificationMessage message, CancellationToken ct = default)
    {
        List<Notification> targets;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            targets = await db.Notifications
                .Include(n => n.Tags)
                .Where(n => n.Enabled)
                .ToListAsync(ct);

            if (targets.Any(t => t.Tags.Count > 0) && await SeriesTagsAsync(db, message, ct) is { } seriesTags)
            {
                targets = targets
                    .Where(t => t.Tags.Count == 0 || t.Tags.Any(tag => seriesTags.Contains(tag.TagId)))
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load notification connections for {Event}", type);
            return;
        }

        var wanted = targets.Where(c => WantsEvent(c, type)).ToList();
        if (wanted.Count == 0)
        {
            return;
        }

        message = await LabelAsync(message, ct);
        foreach (var connection in wanted)
        {
            try
            {
                await SendCoreAsync(connection, message, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Notification '{Name}' ({Type}) failed for {Event}",
                    connection.Name, connection.Type, type);
            }
        }
    }

    /// <summary>
    /// The tags of the series a message is about, or null for an instance-wide message, which tag
    /// scoping never filters.
    /// </summary>
    private static async Task<HashSet<int>?> SeriesTagsAsync(MakiDbContext db, NotificationMessage message, CancellationToken ct)
    {
        if (message.SeriesTagIds is { } carried)
        {
            return [.. carried];
        }

        if (message.SeriesId is not { } seriesId)
        {
            return null;
        }

        return [.. await db.SeriesTags.Where(st => st.SeriesId == seriesId).Select(st => st.TagId).ToListAsync(ct)];
    }

    /// <summary>Sends to a single connection; throws on failure (used by the Test endpoint).</summary>
    public async Task SendToAsync(Notification connection, NotificationMessage message, CancellationToken ct = default) =>
        await SendCoreAsync(connection, await LabelAsync(message, ct), ct);

    private async Task SendCoreAsync(Notification connection, NotificationMessage message, CancellationToken ct)
    {
        if (!_providers.TryGetValue(connection.Type, out var provider))
        {
            throw new InvalidOperationException($"No provider for notification type {connection.Type}");
        }

        await provider.SendAsync(connection, message, ct);
    }

    /// <summary>
    /// The "Series"/"Chapter" labels providers print, in <c>ui.defaultlanguage</c>: the recipient is
    /// a channel, not a person. Falls back to the record's English defaults when localization is absent.
    /// </summary>
    private async Task<NotificationMessage> LabelAsync(NotificationMessage message, CancellationToken ct)
    {
        if (locales is null || catalog is null)
        {
            return message;
        }

        try
        {
            var locale = await locales.DefaultAsync(ct);
            return message with
            {
                SeriesLabel = catalog.GetFor(locale, "notify.label.series"),
                ChapterLabel = catalog.GetFor(locale, "notify.label.chapter")
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not resolve notification labels; using English");
            return message;
        }
    }

    private static bool WantsEvent(Notification c, NotificationEventType type) => type switch
    {
        NotificationEventType.ChapterDownloaded => c.OnChapterDownloaded,
        NotificationEventType.DownloadFailed => c.OnDownloadFailed,
        NotificationEventType.NewChapterAvailable => c.OnNewChapterAvailable,
        NotificationEventType.ImportCompleted => c.OnImportCompleted,
        NotificationEventType.HealthIssue => c.OnHealthIssue,
        NotificationEventType.UpdateAvailable => c.OnUpdateAvailable,
        NotificationEventType.SeriesAdded => c.OnSeriesAdded,
        NotificationEventType.SeriesRemoved => c.OnSeriesRemoved,
        NotificationEventType.RequestSubmitted => c.OnRequestSubmitted,
        NotificationEventType.RequestResolved => c.OnRequestResolved,
        NotificationEventType.ManualMatchNeeded => c.OnManualMatchNeeded,
        NotificationEventType.Test => true,
        _ => false
    };
}
