using Maki.Core.Notifications;

namespace Maki.Core.Inbox;

/// <summary>
/// One in-app notification, before it is fanned out to recipients. Reuses
/// <see cref="NotificationLevel"/> rather than declaring a second three-value severity enum — the
/// meaning is identical and the UI maps both to the same colours.
/// </summary>
/// <param name="Url">
/// A path inside the app (<c>/series/42</c>), never an absolute URL. The bell turns it into a router
/// navigation, so an absolute one would take the user out of the SPA.
/// </param>
public record InboxMessage(
    string Title,
    string Body,
    NotificationLevel Level = NotificationLevel.Info,
    int? SeriesId = null,
    int? ChapterId = null,
    string? Url = null);
