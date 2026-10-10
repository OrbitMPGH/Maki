using System.Globalization;
using Maki.Api.Localization;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Notifications;
using Maki.Data;

namespace Maki.Api.Services;

/// <summary>
/// Writes <see cref="SeriesMetadataChange"/> rows and announces status changes.
/// <para>
/// Rows are added to the request's own <see cref="MakiDbContext"/>, so they commit with the
/// metadata they describe and a refresh that fails to save leaves no history behind. The
/// announcements wait for <see cref="PublishAsync"/>, which the caller runs after that save, so
/// nobody is told about a change that was then rolled back.
/// </para>
/// </summary>
public class SeriesMetadataChangeLog(
    MakiDbContext db,
    InboxService inbox,
    NotificationService notifications,
    IUserLocaleResolver locales,
    ILocalizer localizer,
    TimeProvider time,
    ILogger<SeriesMetadataChangeLog> logger)
{
    private readonly List<StatusChange> _pending = [];

    private sealed record StatusChange(int SeriesId, int RootFolderId, string Title, SeriesStatus From, SeriesStatus To);

    /// <summary>The values a refresh may change, read before it runs.</summary>
    public readonly record struct Snapshot(string Title, SeriesStatus Status, int? TotalChapters, int? TotalVolumes)
    {
        public static Snapshot Of(Series series) =>
            new(series.Title, series.Status, series.TotalChapters, series.TotalVolumes);
    }

    /// <summary>Records each tracked field that differs between <paramref name="before"/> and the series now.</summary>
    public void RecordRefresh(Series series, Snapshot before)
    {
        Record(series, SeriesMetadataField.Title, before.Title, series.Title, MetadataChangeSource.Refresh);
        RecordStatus(series, before.Status, series.Status, MetadataChangeSource.Refresh);
        Record(series, SeriesMetadataField.TotalChapters, Text(before.TotalChapters), Text(series.TotalChapters), MetadataChangeSource.Refresh);
        Record(series, SeriesMetadataField.TotalVolumes, Text(before.TotalVolumes), Text(series.TotalVolumes), MetadataChangeSource.Refresh);
    }

    /// <summary>
    /// Records a status change and queues its announcement. A move to or from Unknown is recorded
    /// but not announced: it means the provider lost or first found the value, not that the series changed.
    /// </summary>
    public void RecordStatus(Series series, SeriesStatus from, SeriesStatus to, MetadataChangeSource source, int? userId = null)
    {
        if (from == to)
        {
            return;
        }

        Record(series, SeriesMetadataField.Status, from.ToString(), to.ToString(), source, userId);
        if (from != SeriesStatus.Unknown && to != SeriesStatus.Unknown)
        {
            _pending.Add(new StatusChange(series.Id, series.RootFolderId, series.Title, from, to));
        }
    }

    /// <summary>Adds a row when the value really changed.</summary>
    public void Record(
        Series series, SeriesMetadataField field, string? from, string? to,
        MetadataChangeSource source, int? userId = null)
    {
        if (!string.Equals(from, to, StringComparison.Ordinal))
        {
            Add(series, field, from, to, source, userId);
        }
    }

    /// <summary>
    /// A user changed a field whose value does not fit in the history (the synopsis, the poster).
    /// The row says that it changed and who changed it, without either value.
    /// </summary>
    public void RecordReplaced(Series series, SeriesMetadataField field, int? userId) =>
        Add(series, field, null, null, MetadataChangeSource.User, userId);

    private void Add(
        Series series, SeriesMetadataField field, string? from, string? to,
        MetadataChangeSource source, int? userId)
    {
        db.SeriesMetadataChanges.Add(new SeriesMetadataChange
        {
            SeriesId = series.Id,
            Field = field,
            OldValue = from,
            NewValue = to,
            Source = source,
            UserId = source == MetadataChangeSource.User ? userId : null,
            ChangedAtUtc = time.GetUtcNow().UtcDateTime,
        });
    }

    public static string? Text(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Raises the inbox row and the outbound message for every status change recorded so far, then
    /// forgets them. Call after the save that committed them.
    /// </summary>
    public async Task PublishAsync(CancellationToken ct = default)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var changes = _pending.ToList();
        _pending.Clear();

        string? locale = null;
        foreach (var change in changes)
        {
            var from = StatusKey(change.From);
            var to = StatusKey(change.To);

            // {series} is filled at read time from SeriesId, so each reader sees their own display title.
            inbox.Raise(InboxEventType.SeriesStatusChanged, new InboxMessage(
                    Key: "inbox.series.statusChanged",
                    Params: InboxMessage.Args(new { from, to }),
                    SeriesId: change.SeriesId,
                    Url: $"/series/{change.SeriesId}"),
                InboxAudience.SeriesTrackers(change.SeriesId, change.RootFolderId));

            try
            {
                locale ??= await locales.DefaultAsync(ct);
                notifications.Dispatch(NotificationEventType.SeriesStatusChanged, new NotificationMessage(
                    NotificationEventType.SeriesStatusChanged,
                    Title: localizer.GetFor(locale, "notify.series.statusChanged.title"),
                    Body: localizer.GetFor(locale, "notify.series.statusChanged.body", new { series = change.Title, from, to }),
                    SeriesTitle: change.Title,
                    SeriesId: change.SeriesId,
                    Url: $"/series/{change.SeriesId}"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not send the status change notification for series {SeriesId}", change.SeriesId);
            }
        }
    }

    /// <summary>The <c>select</c> key the catalogue words a status by.</summary>
    internal static string StatusKey(SeriesStatus status) => status.ToString().ToLowerInvariant();
}
