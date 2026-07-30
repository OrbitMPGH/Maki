using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maki.Core.Entities;

namespace Maki.Core.Notifications;

/// <summary>
/// Posts a Discord embed to a channel webhook URL.
/// <para>
/// The series poster is <b>uploaded with the message</b> and referenced as
/// <c>attachment://poster.jpg</c> rather than linked: a self-hosted Maki is normally not reachable
/// from Discord's CDN, so an <c>/api/v1/mediacover</c> URL would render as a broken image for most
/// installs. That turns the request into multipart/form-data; without a poster it stays plain JSON.
/// </para>
/// </summary>
public class DiscordNotificationProvider(
    IHttpClientFactory httpClientFactory,
    INotificationCoverStore? covers = null) : INotificationProvider
{
    public const string HttpClientName = "notifications";

    /// <summary>Name the attachment is referenced by from the embed. Must match the multipart filename.</summary>
    private const string PosterFileName = "poster.jpg";

    // Discord's own limits; exceeding any one of them rejects the whole message with a 400.
    private const int TitleLimit = 256;
    private const int DescriptionLimit = 4096;
    private const int FieldValueLimit = 1024;

    /// <summary>Body prefixes that repeat the series title, which becomes the embed title instead.</summary>
    private static readonly string[] SeriesSeparators = [" — ", " - ", ": "];

    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public NotificationType Type => NotificationType.Discord;

    public async Task SendAsync(Notification connection, NotificationMessage message, CancellationToken ct = default)
    {
        var config = NotificationConfig.Discord(connection.ConfigJson);
        if (string.IsNullOrWhiteSpace(config.WebhookUrl))
        {
            throw new InvalidOperationException("Discord webhook URL is not configured");
        }

        var poster = ReadPoster(message);

        using var request = new HttpRequestMessage(HttpMethod.Post, config.WebhookUrl)
        {
            Content = poster is null
                ? JsonContent.Create(Payload(message, withPoster: false), options: PayloadOptions)
                : MultipartWithPoster(message, poster)
        };

        var client = httpClientFactory.CreateClient(HttpClientName);
        var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Loads the series poster, or null when there is none. Failures are swallowed: an unreadable
    /// cover must cost the message its image, never its delivery.
    /// </summary>
    private byte[]? ReadPoster(NotificationMessage message)
    {
        if (covers is null || message.SeriesId is not { } seriesId)
        {
            return null;
        }

        try
        {
            var path = covers.PosterPathFor(seriesId);
            return path is null ? null : File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static MultipartFormDataContent MultipartWithPoster(NotificationMessage message, byte[] poster)
    {
        var form = new MultipartFormDataContent
        {
            {
                new StringContent(
                    JsonSerializer.Serialize(Payload(message, withPoster: true), PayloadOptions),
                    Encoding.UTF8,
                    "application/json"),
                "payload_json"
            }
        };

        var file = new ByteArrayContent(poster);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "files[0]", PosterFileName);
        return form;
    }

    private static object Payload(NotificationMessage message, bool withPoster) => new
    {
        username = "Maki",
        embeds = new[] { Embed(message, withPoster) }
    };

    /// <summary>
    /// The event headline lives in the author line and the series in the title, so the two never
    /// duplicate each other. <c>author.url</c> mirrors <c>url</c> because Discord drops an embed's
    /// url when it has no title — which is every event that isn't about a series.
    /// </summary>
    private static object Embed(NotificationMessage message, bool withPoster) => new
    {
        author = new
        {
            name = Truncate($"{IconFor(message.EventType)} {message.Title}", TitleLimit),
            url = message.Url
        },
        title = Truncate(NullIfBlank(message.SeriesTitle), TitleLimit),
        url = message.Url,
        description = Truncate(Description(message), DescriptionLimit),
        color = ColorFor(message),
        thumbnail = withPoster ? new { url = $"attachment://{PosterFileName}" } : null,
        fields = Fields(message),
        footer = new { text = "Maki" },
        timestamp = DateTimeOffset.UtcNow.ToString("o")
    };

    /// <summary>
    /// Drops the leading "{series} — " the senders write into every body: the series is the embed's
    /// title here, and repeating it one line below reads like a bug.
    /// </summary>
    private static string? Description(NotificationMessage message)
    {
        var body = message.Body;
        if (NullIfBlank(message.SeriesTitle) is { } series)
        {
            foreach (var separator in SeriesSeparators)
            {
                var prefix = series + separator;
                if (body.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    body = body[prefix.Length..];
                    break;
                }
            }
        }

        return NullIfBlank(body);
    }

    /// <summary>
    /// Only the chapter: the series is the embed title, so a "Series" field would repeat it. Null
    /// rather than an empty array so the key is dropped instead of rendering an empty field row.
    /// </summary>
    private static object[]? Fields(NotificationMessage message) =>
        NullIfBlank(message.ChapterNumber) is { } chapter
            ? [new { name = "Chapter", value = Truncate(chapter, FieldValueLimit), inline = true }]
            : null;

    /// <summary>
    /// Discord embed colors are 24-bit ints. Level wins when it is not the default, so a failed
    /// download stays red however its event is categorised; otherwise the event picks the color.
    /// </summary>
    private static int ColorFor(NotificationMessage message) => message.Level switch
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

    private static string IconFor(NotificationEventType eventType) => eventType switch
    {
        NotificationEventType.ChapterDownloaded => "📥",
        NotificationEventType.DownloadFailed => "❌",
        NotificationEventType.NewChapterAvailable => "🆕",
        NotificationEventType.ImportCompleted => "📦",
        NotificationEventType.HealthIssue => "⚠️",
        NotificationEventType.UpdateAvailable => "🚀",
        _ => "🔔"
    };

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int limit) =>
        value is null || value.Length <= limit ? value : value[..(limit - 1)] + "…";
}
