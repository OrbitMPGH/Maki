using Maki.Core.Entities;

namespace Maki.Core.Notifications;

public enum NotificationFieldKind
{
    Text,
    Secret,
    Url,
    Number,
    Boolean
}

/// <summary>One config field a provider needs. <c>Key</c> is the property name inside <c>ConfigJson</c>.</summary>
public record NotificationField(
    string Key,
    NotificationFieldKind Kind,
    bool Required = false,
    string? Placeholder = null,
    int? Min = null,
    int? Max = null);

/// <summary>
/// What the settings UI needs to render and validate a connection form for one provider, so
/// adding a provider never touches the controller or the frontend form. Field labels and help
/// text live in the frontend catalogue keyed by <c>Type</c> and <c>Key</c>, not here.
/// </summary>
public record NotificationProviderDescriptor(
    NotificationType Type,
    IReadOnlyList<NotificationField> Fields,
    /// <summary>Provider can render the series poster with the message.</summary>
    bool SupportsPoster = false);
