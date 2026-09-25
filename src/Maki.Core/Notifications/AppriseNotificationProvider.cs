using System.Net.Http.Json;
using System.Text;
using Maki.Core.Entities;

namespace Maki.Core.Notifications;

/// <summary>
/// Posts to a self-hosted Apprise API server (https://github.com/caronc/apprise-api). Either a
/// stored config key on the server or an inline comma-separated list of Apprise URLs targets the
/// notification; the two routes hit different endpoints on the same server.
/// </summary>
public class AppriseNotificationProvider(IHttpClientFactory httpClientFactory) : INotificationProvider
{
    public NotificationType Type => NotificationType.Apprise;

    public NotificationProviderDescriptor Descriptor { get; } = new(
        NotificationType.Apprise,
        [
            new NotificationField("serverUrl", NotificationFieldKind.Url, Required: true, Placeholder: "https://apprise.example.com"),
            new NotificationField("configKey", NotificationFieldKind.Text),
            new NotificationField("urls", NotificationFieldKind.Secret, Placeholder: "discord://..., mailto://..."),
            new NotificationField("tag", NotificationFieldKind.Text)
        ]);

    public const string TargetRequiredKey = "error.notifications.appriseTarget";

    public string? Validate(NotificationFields fields) =>
        fields["configKey"] is null && fields["urls"] is null ? TargetRequiredKey : null;

    public async Task SendAsync(Notification connection, NotificationMessage message, CancellationToken ct = default)
    {
        var fields = NotificationConfig.Fields(connection.ConfigJson);
        var serverUrl = fields.Require("serverUrl").TrimEnd('/');
        var configKey = fields["configKey"];
        var urls = fields["urls"];
        var tag = fields["tag"];

        if (configKey is null && urls is null)
        {
            throw new InvalidOperationException("Apprise config requires either a config key or a list of URLs");
        }

        var client = httpClientFactory.CreateClient(DiscordNotificationProvider.HttpClientName);
        var title = message.Title;
        var body = Body(message);
        var type = TypeFor(message);

        HttpResponseMessage response;
        if (configKey is not null)
        {
            object payload = tag is null
                ? new { title, body, type, format = "text" }
                : new { title, body, type, format = "text", tag };
            response = await client.PostAsJsonAsync($"{serverUrl}/notify/{Uri.EscapeDataString(configKey)}", payload, ct);
        }
        else
        {
            response = await client.PostAsJsonAsync(
                $"{serverUrl}/notify",
                new { urls, title, body, type, format = "text" },
                ct);
        }

        try
        {
            NotificationDeliveryException.ThrowIfFailed("Apprise", response);
        }
        finally
        {
            response.Dispose();
        }
    }

    private static string Body(NotificationMessage message)
    {
        var sb = new StringBuilder(message.Body);

        if (!string.IsNullOrWhiteSpace(message.SeriesTitle))
        {
            sb.Append('\n').Append(message.SeriesLabel).Append(": ").Append(message.SeriesTitle);
        }

        if (!string.IsNullOrWhiteSpace(message.ChapterNumber))
        {
            sb.Append('\n').Append(message.ChapterLabel).Append(": ").Append(message.ChapterNumber);
        }

        if (Uri.IsWellFormedUriString(message.Url, UriKind.Absolute))
        {
            sb.Append('\n').Append(message.Url);
        }

        return sb.ToString();
    }

    private static string TypeFor(NotificationMessage message) => message.Level switch
    {
        NotificationLevel.Error => "failure",
        NotificationLevel.Warning => "warning",
        _ => message.EventType switch
        {
            NotificationEventType.ChapterDownloaded => "success",
            NotificationEventType.ImportCompleted => "success",
            _ => "info"
        }
    };
}
