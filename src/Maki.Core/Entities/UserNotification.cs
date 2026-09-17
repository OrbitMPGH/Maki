using Maki.Core.Inbox;
using Maki.Core.Notifications;
using Maki.Core.Security;

namespace Maki.Core.Entities;

/// <summary>
/// One in-app notification delivered to one user: a row per recipient, not a row per event with a
/// join table. Read state, dismissal and retention are all per person, so a shared row would need
/// every one of those in a side table anyway, and the fan-out here is a handful of users.
/// <para>
/// Distinct from <see cref="Notification"/>, which despite the name is an outbound Discord/webhook
/// <em>connection</em> — instance-wide, admin-managed, no recipient. Backend code calls this side
/// "inbox" to keep the two apart; the UI calls it Notifications, because that is the word a reader
/// expects.
/// </para>
/// </summary>
public class UserNotification : IUserOwned
{
    public int Id { get; set; }

    public int UserId { get; set; }

    public InboxEventType Type { get; set; }

    public NotificationLevel Level { get; set; }

    /// <summary>
    /// The catalogue key this row renders from, naming <c>{MessageKey}.title</c> and
    /// <c>{MessageKey}.body</c>. Null on rows written before notifications were keyed, which is why
    /// <see cref="Title"/> and <see cref="Body"/> are still here.
    /// <para>
    /// Not <see cref="InboxEventType"/>, and it does not inherit that enum's append-only rule. See
    /// <see cref="InboxMessage.Key"/> for why the two are deliberately different grains.
    /// </para>
    /// </summary>
    public string? MessageKey { get; set; }

    /// <summary>
    /// JSON object of the values filling the message's placeholders, or null when it has none.
    /// Deliberately not the series title: that is resolved per reader at render time from
    /// <see cref="SeriesId"/>, so two people who prefer different title languages each see their own.
    /// </summary>
    public string? ParamsJson { get; set; }

    /// <summary>
    /// English, written at raise time. It is what a row created before this was keyed renders as,
    /// and the fallback when a key has somehow left the catalogue. Once retention has aged the
    /// pre-migration rows out (30 days or 200 rows, whichever comes first, in
    /// <c>HousekeepingJob</c>), this and <see cref="Body"/> can go.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>See <see cref="Title"/>.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// What the notification is about, for grouping and for the cover thumbnail. Deliberately not a
    /// foreign key: deleting a series should break the link, not erase the record that its chapters
    /// once downloaded. <see cref="Url"/> is resolved against the live library at render time, so a
    /// dangling id simply renders as unlinked text.
    /// </summary>
    public int? SeriesId { get; set; }

    /// <summary>Same reasoning as <see cref="SeriesId"/>.</summary>
    public int? ChapterId { get; set; }

    /// <summary>A path inside the SPA, e.g. <c>/series/42</c>. Null when there is nowhere to go.</summary>
    public string? Url { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Null until the user has seen it. Drives the bell's unread badge.</summary>
    public DateTime? ReadAt { get; set; }
}
