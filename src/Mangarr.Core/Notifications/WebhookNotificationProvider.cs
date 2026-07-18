using System.Net.Http.Headers;
using System.Net.Http.Json;
using Mangarr.Core.Entities;

namespace Mangarr.Core.Notifications;

/// <summary>POSTs the message as JSON to a user-supplied URL, with an optional bearer token.</summary>
public class WebhookNotificationProvider(IHttpClientFactory httpClientFactory) : INotificationProvider
{
    public NotificationType Type => NotificationType.Webhook;

    public async Task SendAsync(Notification connection, NotificationMessage message, CancellationToken ct = default)
    {
        var config = NotificationConfig.Webhook(connection.ConfigJson);
        if (string.IsNullOrWhiteSpace(config.Url))
        {
            throw new InvalidOperationException("Webhook URL is not configured");
        }

        var payload = new
        {
            eventType = message.EventType.ToString(),
            level = message.Level.ToString(),
            title = message.Title,
            body = message.Body,
            seriesTitle = message.SeriesTitle,
            seriesId = message.SeriesId,
            chapterNumber = message.ChapterNumber,
            url = message.Url
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, config.Url)
        {
            Content = JsonContent.Create(payload)
        };
        if (!string.IsNullOrWhiteSpace(config.BearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.BearerToken);
        }

        var client = httpClientFactory.CreateClient(DiscordNotificationProvider.HttpClientName);
        var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }
}
