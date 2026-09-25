using System.Net.Http.Headers;
using Maki.Core.Entities;

namespace Maki.Core.Notifications;

/// <summary>
/// Posts to the Pushover API as multipart form data. Priority 2 ("emergency") requires Pushover's
/// retry/expire pair on every request or the API rejects it outright, so those are sent whenever
/// the configured priority is 2 rather than left for the user to remember.
/// </summary>
public class PushoverNotificationProvider(
    IHttpClientFactory httpClientFactory,
    INotificationCoverStore? covers = null) : INotificationProvider
{
    private const string Endpoint = "https://api.pushover.net/1/messages.json";
    private const int MessageLimit = 1024;
    private const int TitleLimit = 250;
    private const int UrlLimit = 512;
    private const int UrlTitleLimit = 100;
    private const int EmergencyPriority = 2;

    public NotificationType Type => NotificationType.Pushover;

    public NotificationProviderDescriptor Descriptor { get; } = new(
        NotificationType.Pushover,
        [
            new NotificationField("appToken", NotificationFieldKind.Secret, Required: true),
            new NotificationField("userKey", NotificationFieldKind.Secret, Required: true),
            new NotificationField("priority", NotificationFieldKind.Number, Min: -2, Max: 2),
            new NotificationField("device", NotificationFieldKind.Text)
        ],
        SupportsPoster: true);

    public async Task SendAsync(Notification connection, NotificationMessage message, CancellationToken ct = default)
    {
        var fields = NotificationConfig.Fields(connection.ConfigJson);
        var appToken = fields.Require("appToken");
        var userKey = fields.Require("userKey");
        var priority = fields.Int("priority");
        var device = fields["device"];

        // Pushover rejects a url past its limit, and a cut URL points nowhere, so it is dropped instead.
        var absoluteUrl = Uri.IsWellFormedUriString(message.Url, UriKind.Absolute) && message.Url!.Length <= UrlLimit
            ? message.Url
            : null;

        using var form = new MultipartFormDataContent
        {
            { new StringContent(appToken), "token" },
            { new StringContent(userKey), "user" },
            { new StringContent(Truncate(message.Title, TitleLimit)), "title" },
            { new StringContent(Truncate(Body(message), MessageLimit)), "message" }
        };

        if (priority is not null)
        {
            form.Add(new StringContent(priority.Value.ToString()), "priority");
            if (priority.Value == EmergencyPriority)
            {
                form.Add(new StringContent("60"), "retry");
                form.Add(new StringContent("3600"), "expire");
            }
        }

        if (!string.IsNullOrWhiteSpace(device))
        {
            form.Add(new StringContent(device), "device");
        }

        if (absoluteUrl is not null)
        {
            form.Add(new StringContent(absoluteUrl), "url");
            if (!string.IsNullOrWhiteSpace(message.SeriesTitle))
            {
                form.Add(new StringContent(Truncate(message.SeriesTitle, UrlTitleLimit)), "url_title");
            }
        }

        var poster = NotificationPoster.Read(covers, message);
        if (poster is not null)
        {
            var file = new ByteArrayContent(poster);
            file.Headers.ContentType = new MediaTypeHeaderValue(NotificationPoster.ContentType);
            form.Add(file, "attachment", NotificationPoster.FileName);
        }

        var client = httpClientFactory.CreateClient(DiscordNotificationProvider.HttpClientName);
        using var response = await client.PostAsync(Endpoint, form, ct);
        NotificationDeliveryException.ThrowIfFailed("Pushover", response);
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

    private static string Truncate(string value, int limit) =>
        value.Length <= limit ? value : value[..(limit - 1)] + "…";
}
