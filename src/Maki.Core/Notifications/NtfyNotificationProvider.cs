using System.Net.Http.Headers;
using System.Net.Http.Json;
using Maki.Core.Entities;

namespace Maki.Core.Notifications;

/// <summary>
/// Posts to an ntfy server (ntfy.sh or self-hosted) via its JSON publish endpoint rather than the
/// plain-text topic URL, because ntfy headers are restricted to ASCII and a title in any other
/// script would otherwise have to be dropped or mangled.
/// </summary>
public class NtfyNotificationProvider(IHttpClientFactory httpClientFactory) : INotificationProvider
{
    public NotificationType Type => NotificationType.Ntfy;

    public NotificationProviderDescriptor Descriptor { get; } = new(
        NotificationType.Ntfy,
        [
            new NotificationField("serverUrl", NotificationFieldKind.Url, Required: true, Placeholder: "https://ntfy.sh"),
            new NotificationField("topic", NotificationFieldKind.Text, Required: true),
            new NotificationField("token", NotificationFieldKind.Secret),
            new NotificationField("priority", NotificationFieldKind.Number, Min: 1, Max: 5)
        ]);

    public async Task SendAsync(Notification connection, NotificationMessage message, CancellationToken ct = default)
    {
        var fields = NotificationConfig.Fields(connection.ConfigJson);
        var serverUrl = fields.Require("serverUrl").TrimEnd('/');
        var topic = fields.Require("topic");
        var token = fields["token"];
        var priority = fields.Int("priority") ?? DefaultPriority(message);

        var payload = new Dictionary<string, object?>
        {
            ["topic"] = topic,
            ["title"] = message.Title,
            ["message"] = Body(message),
            ["priority"] = priority,
            ["tags"] = new[] { TagFor(message) }
        };

        if (Uri.IsWellFormedUriString(message.Url, UriKind.Absolute))
        {
            payload["click"] = message.Url;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, serverUrl)
        {
            Content = JsonContent.Create(payload)
        };
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        var client = httpClientFactory.CreateClient(DiscordNotificationProvider.HttpClientName);
        using var response = await client.SendAsync(request, ct);
        NotificationDeliveryException.ThrowIfFailed("ntfy", response);
    }

    private static string Body(NotificationMessage message)
    {
        var lines = new List<string> { message.Body };
        if (!string.IsNullOrWhiteSpace(message.SeriesTitle))
        {
            lines.Add($"{message.SeriesLabel}: {message.SeriesTitle}");
        }
        if (!string.IsNullOrWhiteSpace(message.ChapterNumber))
        {
            lines.Add($"{message.ChapterLabel}: {message.ChapterNumber}");
        }
        return string.Join('\n', lines);
    }

    /// <summary>ntfy's 1 to 5 scale: 4 makes a failure or health problem buzz, 3 is ntfy's default.</summary>
    internal static int DefaultPriority(NotificationMessage message) =>
        message.Level == NotificationLevel.Error ||
        message.EventType is NotificationEventType.DownloadFailed or NotificationEventType.HealthIssue
            ? 4
            : 3;

    private static string TagFor(NotificationMessage message) => message.Level switch
    {
        NotificationLevel.Error => "x",
        NotificationLevel.Warning => "warning",
        _ => EventTag(message.EventType)
    };

    /// <summary>ntfy turns a tag that names an emoji short code into that emoji in front of the title.</summary>
    internal static string EventTag(NotificationEventType eventType) => eventType switch
    {
        NotificationEventType.Test => "bell",
        NotificationEventType.ChapterDownloaded => "inbox_tray",
        NotificationEventType.DownloadFailed => "x",
        NotificationEventType.NewChapterAvailable => "new",
        NotificationEventType.ImportCompleted => "package",
        NotificationEventType.HealthIssue => "warning",
        NotificationEventType.UpdateAvailable => "rocket",
        NotificationEventType.SeriesAdded => "heavy_plus_sign",
        NotificationEventType.SeriesRemoved => "wastebasket",
        NotificationEventType.RequestSubmitted => "raising_hand",
        NotificationEventType.RequestResolved => "white_check_mark",
        NotificationEventType.ManualMatchNeeded => "mag",
        NotificationEventType.SeriesStatusChanged => "arrows_counterclockwise",
        NotificationEventType.SeriesArchived => "card_file_box",
        _ => "bell"
    };
}
