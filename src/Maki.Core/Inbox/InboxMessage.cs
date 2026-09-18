using Maki.Core.Notifications;

namespace Maki.Core.Inbox;

/// <summary>
/// One in-app notification, before it is fanned out to recipients. Reuses
/// <see cref="NotificationLevel"/> rather than declaring a second three-value severity enum — the
/// meaning is identical and the UI maps both to the same colours.
/// </summary>
/// <param name="Key">
/// The catalogue key for this message, naming two entries: <c>{Key}.title</c> and <c>{Key}.body</c>.
/// One key for the pair rather than two fields halves the bookkeeping, and the two halves of a
/// notification never come from different messages anyway.
/// <para>
/// <b>This is not <c>InboxEventType</c> and does not inherit its rule.</b> That enum's values are
/// persisted in <c>UserNotifications.Type</c> <em>and</em> as keys inside every user's
/// <c>notifications.inbox</c> preferences blob, so it is append-only and must never be renumbered
/// (see <c>.claude/rules/stats-notifications.md</c>). A key here is finer-grained, because one event
/// type raises several different sentences: <c>DownloadFailed</c> is raised both for a single
/// chapter and for a batch, and <c>RequestResolved</c> reads differently approved than declined. It
/// is pure presentation and can be renamed freely, as long as the catalogue is renamed with it.
/// </para>
/// </param>
/// <param name="Params">
/// Values filling the message's ICU placeholders. Carry only what cannot be re-derived at read time.
/// <para>
/// In particular, <b>never put a series title in here</b>. Storing it freezes the title as it read
/// on the day, and this app deliberately resolves a series' display title per user: somebody whose
/// <c>ui.titlelanguage</c> is Japanese expects to see the Japanese title in their bell too. Pass
/// <see cref="SeriesId"/> instead, which is a column already, and let the render resolve
/// <c>{series}</c> against the live library.
/// </para>
/// </param>
/// <param name="Url">
/// A path inside the app (<c>/series/42</c>), never an absolute URL. The bell turns it into a router
/// navigation, so an absolute one would take the user out of the SPA.
/// </param>
public record InboxMessage(
    string Key,
    IReadOnlyDictionary<string, object?>? Params = null,
    NotificationLevel Level = NotificationLevel.Info,
    int? SeriesId = null,
    int? ChapterId = null,
    string? Url = null,
    string? UnkeyedTitle = null,
    string? UnkeyedBody = null)
{
    /// <summary>
    /// A notification whose text is not in the catalogue yet, carried through as English.
    /// <para>
    /// This exists for the health checks, whose sentences are assembled from findings that are not
    /// keyed themselves; converting them is a change of its own. It renders through exactly the same
    /// path as a row written before notifications were keyed, so there is no second code path to
    /// keep working. Do not reach for it for anything new: a new notification gets a key.
    /// </para>
    /// </summary>
    public static InboxMessage Unkeyed(
        string title,
        string body,
        NotificationLevel level = NotificationLevel.Info,
        string? url = null,
        int? seriesId = null,
        int? chapterId = null) =>
        new(string.Empty, Level: level, SeriesId: seriesId, ChapterId: chapterId, Url: url,
            UnkeyedTitle: title, UnkeyedBody: body);

    /// <summary>
    /// Convenience for the common shape, so a call site can write the parameters inline without
    /// naming a dictionary type:
    /// <c>new InboxMessage("inbox.chapter.downloaded", InboxMessage.Args(new { label }))</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> Args(object values) =>
        values.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(values));
}
