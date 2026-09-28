namespace Maki.Core.Entities;

/// <summary>Scopes an outbound <see cref="Notification"/> connection to series carrying this <see cref="Tag"/>.</summary>
public class NotificationTag
{
    public int NotificationId { get; set; }
    public int TagId { get; set; }
}
