using System.Globalization;
using System.Net.Http.Json;
using Maki.Core.Entities;

namespace Maki.Core.Notifications;

/// <summary>
/// Posts to Notifiarr's Discord passthrough endpoint. The API key lives in the URL path, so any
/// failure message must not carry the request URI (Telegram's provider has the same rule).
/// See https://notifiarr.wiki/pages/integrations/passthrough/.
/// </summary>
public class NotifiarrNotificationProvider(IHttpClientFactory httpClientFactory) : INotificationProvider
{
    public NotificationType Type => NotificationType.Notifiarr;

    public NotificationProviderDescriptor Descriptor { get; } = new(
        NotificationType.Notifiarr,
        [
            new NotificationField("apiKey", NotificationFieldKind.Secret, Required: true),
            new NotificationField("channelId", NotificationFieldKind.Text, Required: true)
        ]);

    public const string ChannelIdKey = "error.notifications.notifiarrChannelId";

    public string? Validate(NotificationFields fields) =>
        fields["channelId"] is { } channelId && !long.TryParse(channelId, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) ? ChannelIdKey : null;

    public async Task SendAsync(Notification connection, NotificationMessage message, CancellationToken ct = default)
    {
        var fields = NotificationConfig.Fields(connection.ConfigJson);
        var apiKey = fields.Require("apiKey");
        var channelIdText = fields.Require("channelId");
        if (!long.TryParse(channelIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var channelId))
        {
            throw new InvalidOperationException("Notifiarr channel id is not a valid number");
        }

        var payload = new
        {
            notification = new
            {
                name = "Maki",
                @event = message.EventType.ToString(),
                update = false
            },
            discord = new
            {
                color = ColorFor(message),
                text = new
                {
                    title = message.Title,
                    description = message.Body,
                    fields = Fields(message),
                    footer = "Maki"
                },
                ids = new { channel = channelId }
            }
        };

        var client = httpClientFactory.CreateClient(DiscordNotificationProvider.HttpClientName);
        // Notifiarr's docs only document the key-in-path form, not an X-API-Key header.
        using var response = await client.PostAsJsonAsync(
            $"https://notifiarr.com/api/v1/notification/passthrough/{apiKey}", payload, ct);

        NotificationDeliveryException.ThrowIfFailed("Notifiarr", response);
    }

    private static object[]? Fields(NotificationMessage message)
    {
        var fields = new List<object>();
        if (!string.IsNullOrWhiteSpace(message.SeriesTitle))
        {
            fields.Add(new { title = message.SeriesLabel, text = message.SeriesTitle, inline = true });
        }
        if (!string.IsNullOrWhiteSpace(message.ChapterNumber))
        {
            fields.Add(new { title = message.ChapterLabel, text = message.ChapterNumber, inline = true });
        }

        return fields.Count > 0 ? fields.ToArray() : null;
    }

    /// <summary>Same colour mapping as <see cref="DiscordNotificationProvider"/>, as a hex string without '#'.</summary>
    private static string ColorFor(NotificationMessage message)
    {
        var color = message.Level switch
        {
            NotificationLevel.Error => 0xED4245,
            NotificationLevel.Warning => 0xFAA61A,
            _ => message.EventType switch
            {
                NotificationEventType.ChapterDownloaded => 0x57F287,
                NotificationEventType.DownloadFailed => 0xED4245,
                NotificationEventType.NewChapterAvailable => 0x5865F2,
                NotificationEventType.ImportCompleted => 0x1ABC9C,
                NotificationEventType.HealthIssue => 0xFAA61A,
                NotificationEventType.UpdateAvailable => 0x9B59B6,
                _ => 0x5865F2
            }
        };

        return color.ToString("X6");
    }
}
