using System.Net.Http.Json;
using Maki.Core.Entities;

namespace Maki.Core.Notifications;

/// <summary>Posts to a self-hosted Gotify server's message endpoint, authenticated by app token header.</summary>
public class GotifyNotificationProvider(IHttpClientFactory httpClientFactory) : INotificationProvider
{
    public NotificationType Type => NotificationType.Gotify;

    public NotificationProviderDescriptor Descriptor { get; } = new(
        NotificationType.Gotify,
        [
            new NotificationField("serverUrl", NotificationFieldKind.Url, Required: true),
            new NotificationField("appToken", NotificationFieldKind.Secret, Required: true),
            new NotificationField("priority", NotificationFieldKind.Number, Min: 0, Max: 10)
        ]);

    public async Task SendAsync(Notification connection, NotificationMessage message, CancellationToken ct = default)
    {
        var fields = NotificationConfig.Fields(connection.ConfigJson);
        var serverUrl = fields.Require("serverUrl").TrimEnd('/');
        var appToken = fields.Require("appToken");
        var priority = fields.Int("priority") ?? (message.Level == NotificationLevel.Error ? 8 : 5);

        var payload = new
        {
            title = message.Title,
            message = Body(message),
            priority
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{serverUrl}/message")
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Add("X-Gotify-Key", appToken);

        var client = httpClientFactory.CreateClient(DiscordNotificationProvider.HttpClientName);
        var response = await client.SendAsync(request, ct);
        NotificationDeliveryException.ThrowIfFailed("Gotify", response);
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
}
