using System.Net.Http.Json;
using Maki.Core.Entities;

namespace Maki.Core.Notifications;

/// <summary>
/// Posts to a Slack-compatible incoming webhook. Slack, Mattermost, and Rocket.Chat all accept
/// this same <c>{ text, attachments }</c> shape, though only Mattermost honours <c>channel</c> on
/// an incoming webhook: Slack fixes the channel at webhook creation and ignores the field.
/// </summary>
public class SlackWebhookNotificationProvider(IHttpClientFactory httpClientFactory) : INotificationProvider
{
    public NotificationType Type => NotificationType.SlackWebhook;

    public NotificationProviderDescriptor Descriptor { get; } = new(
        NotificationType.SlackWebhook,
        [
            new NotificationField("webhookUrl", NotificationFieldKind.Url, Required: true, Placeholder: "https://hooks.slack.com/services/..."),
            new NotificationField("channel", NotificationFieldKind.Text, Placeholder: "#manga")
        ]);

    public async Task SendAsync(Notification connection, NotificationMessage message, CancellationToken ct = default)
    {
        var fields = NotificationConfig.Fields(connection.ConfigJson);
        var webhookUrl = fields.Require("webhookUrl");
        var channel = fields["channel"];

        var payload = new Dictionary<string, object?>
        {
            ["text"] = $"*{Headline(message)}*\n{Escape(message.Body)}"
        };
        if (channel is not null)
        {
            payload["channel"] = channel;
        }

        var attachment = Attachment(message);
        if (attachment is not null)
        {
            payload["attachments"] = new[] { attachment };
        }

        var client = httpClientFactory.CreateClient(DiscordNotificationProvider.HttpClientName);
        var response = await client.PostAsJsonAsync(webhookUrl, payload, ct);
        NotificationDeliveryException.ThrowIfFailed("Slack", response);
    }

    /// <summary>
    /// With a series the link rides the attachment title. Without one there is no attachment
    /// title for <c>title_link</c> to hang off, so the headline itself becomes the link.
    /// </summary>
    private static string Headline(NotificationMessage message) =>
        string.IsNullOrWhiteSpace(message.SeriesTitle) && AbsoluteUrl(message) is { } url
            ? $"<{url}|{Escape(message.Title)}>"
            : Escape(message.Title);

    private static string? AbsoluteUrl(NotificationMessage message) =>
        Uri.IsWellFormedUriString(message.Url, UriKind.Absolute) ? message.Url : null;

    private static object? Attachment(NotificationMessage message)
    {
        var fields = new List<object>();
        if (!string.IsNullOrWhiteSpace(message.SeriesTitle))
        {
            fields.Add(new { title = Escape(message.SeriesLabel), value = Escape(message.SeriesTitle), @short = true });
        }

        if (!string.IsNullOrWhiteSpace(message.ChapterNumber))
        {
            fields.Add(new { title = Escape(message.ChapterLabel), value = Escape(message.ChapterNumber), @short = true });
        }

        if (fields.Count == 0)
        {
            return null;
        }

        var attachment = new Dictionary<string, object?>
        {
            ["color"] = ColorFor(message),
            ["fields"] = fields
        };
        if (!string.IsNullOrWhiteSpace(message.SeriesTitle))
        {
            attachment["title"] = Escape(message.SeriesTitle);
            if (AbsoluteUrl(message) is { } url)
            {
                attachment["title_link"] = url;
            }
        }

        return attachment;
    }

    /// <summary>Same palette as <see cref="DiscordNotificationProvider"/>'s <c>ColorFor</c>, hex-formatted.</summary>
    private static string ColorFor(NotificationMessage message) => message.Level switch
    {
        NotificationLevel.Error => "#ed4245",
        NotificationLevel.Warning => "#faa61a",
        _ => message.EventType switch
        {
            NotificationEventType.ChapterDownloaded => "#57f287",
            NotificationEventType.DownloadFailed => "#ed4245",
            NotificationEventType.NewChapterAvailable => "#5865f2",
            NotificationEventType.ImportCompleted => "#1abc9c",
            NotificationEventType.HealthIssue => "#faa61a",
            NotificationEventType.UpdateAvailable => "#9b59b6",
            _ => "#5865f2"
        }
    };

    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
