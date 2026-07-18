using Maki.Core.Entities;

namespace Maki.Core.Notifications;

/// <summary>
/// Delivers a <see cref="NotificationMessage"/> to one connection. One implementation per
/// <see cref="NotificationType"/>; resolved by <c>Type</c> at dispatch time. Telegram/Gotify/
/// Apprise slot in as new implementations without touching the dispatcher.
/// </summary>
public interface INotificationProvider
{
    NotificationType Type { get; }

    /// <summary>Sends the message using the connection's <c>ConfigJson</c>. Throws on delivery failure.</summary>
    Task SendAsync(Notification connection, NotificationMessage message, CancellationToken ct = default);
}
