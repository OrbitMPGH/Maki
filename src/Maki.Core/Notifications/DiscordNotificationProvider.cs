using System.Net.Http.Json;
using Maki.Core.Entities;

namespace Maki.Core.Notifications;

/// <summary>Posts a Discord embed to a channel webhook URL.</summary>
public class DiscordNotificationProvider(IHttpClientFactory httpClientFactory) : INotificationProvider
{
    public const string HttpClientName = "notifications";

    public NotificationType Type => NotificationType.Discord;

    public async Task SendAsync(Notification connection, NotificationMessage message, CancellationToken ct = default)
    {
        var config = NotificationConfig.Discord(connection.ConfigJson);
        if (string.IsNullOrWhiteSpace(config.WebhookUrl))
        {
            throw new InvalidOperationException("Discord webhook URL is not configured");
        }

        var embed = new
        {
            title = message.Title,
            description = message.Body,
            color = ColorFor(message.Level),
            url = message.Url,
            fields = Fields(message)
        };

        var payload = new { embeds = new[] { embed } };

        var client = httpClientFactory.CreateClient(HttpClientName);
        var response = await client.PostAsJsonAsync(config.WebhookUrl, payload, ct);
        response.EnsureSuccessStatusCode();
    }

    private static object[] Fields(NotificationMessage message)
    {
        var fields = new List<object>();
        if (message.SeriesTitle is not null)
        {
            fields.Add(new { name = "Series", value = message.SeriesTitle, inline = true });
        }

        if (message.ChapterNumber is not null)
        {
            fields.Add(new { name = "Chapter", value = message.ChapterNumber, inline = true });
        }

        return [.. fields];
    }

    // Discord embed colors are 24-bit ints. Blue / orange / red.
    private static int ColorFor(NotificationLevel level) => level switch
    {
        NotificationLevel.Warning => 0xE67E22,
        NotificationLevel.Error => 0xE74C3C,
        _ => 0x3498DB
    };
}
