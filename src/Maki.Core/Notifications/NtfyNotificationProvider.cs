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
        var priority = fields.Int("priority") ?? (message.Level == NotificationLevel.Error ? 4 : 3);

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

    private static string TagFor(NotificationMessage message)
    {
        if (message.Level == NotificationLevel.Error)
        {
            return "x";
        }
        if (message.Level == NotificationLevel.Warning)
        {
            return "warning";
        }

        return message.EventType switch
        {
            NotificationEventType.ChapterDownloaded => "inbox_tray",
            NotificationEventType.NewChapterAvailable => "new",
            NotificationEventType.ImportCompleted => "package",
            NotificationEventType.UpdateAvailable => "rocket",
            _ => "bell"
        };
    }
}
