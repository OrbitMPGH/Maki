using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Maki.Core.Entities;

namespace Maki.Core.Notifications;

/// <summary>
/// Posts to a Telegram chat via the Bot API. Sends a photo (with caption) when a poster is
/// available, otherwise a plain text message. Both the bot token and chat live in the URL/config,
/// never in the request body, so nothing beyond the field values is exposed to the chat itself.
/// </summary>
public class TelegramNotificationProvider(
    IHttpClientFactory httpClientFactory,
    INotificationCoverStore? covers = null) : INotificationProvider
{
    // Telegram's own limits; a caption or message text past these is rejected outright.
    private const int CaptionLimit = 1024;
    private const int MessageLimit = 4096;

    public NotificationType Type => NotificationType.Telegram;

    public NotificationProviderDescriptor Descriptor { get; } = new(
        NotificationType.Telegram,
        [
            new NotificationField("botToken", NotificationFieldKind.Secret, Required: true),
            new NotificationField("chatId", NotificationFieldKind.Text, Required: true),
            new NotificationField("threadId", NotificationFieldKind.Text),
            new NotificationField("silent", NotificationFieldKind.Boolean)
        ],
        SupportsPoster: true);

    public async Task SendAsync(Notification connection, NotificationMessage message, CancellationToken ct = default)
    {
        var fields = NotificationConfig.Fields(connection.ConfigJson);
        var botToken = fields.Require("botToken");
        var chatId = fields.Require("chatId");
        var threadId = fields["threadId"];
        var silent = fields.Flag("silent");

        var poster = NotificationPoster.Read(covers, message);
        var client = httpClientFactory.CreateClient(DiscordNotificationProvider.HttpClientName);

        HttpResponseMessage response;
        if (poster is not null)
        {
            var text = Text(message, CaptionLimit);
            using var form = new MultipartFormDataContent
            {
                { new StringContent(chatId), "chat_id" },
                { new StringContent(text), "caption" },
                { new StringContent("HTML"), "parse_mode" }
            };
            if (threadId is not null)
            {
                form.Add(new StringContent(threadId), "message_thread_id");
            }
            if (silent)
            {
                form.Add(new StringContent("true"), "disable_notification");
            }

            var file = new ByteArrayContent(poster);
            file.Headers.ContentType = new MediaTypeHeaderValue(NotificationPoster.ContentType);
            form.Add(file, "photo", NotificationPoster.FileName);

            response = await client.PostAsync($"https://api.telegram.org/bot{botToken}/sendPhoto", form, ct);
        }
        else
        {
            var text = Text(message, MessageLimit);
            var payload = new Dictionary<string, object?>
            {
                ["chat_id"] = chatId,
                ["text"] = text,
                ["parse_mode"] = "HTML",
                ["disable_web_page_preview"] = true
            };
            if (threadId is not null)
            {
                payload["message_thread_id"] = threadId;
            }
            if (silent)
            {
                payload["disable_notification"] = true;
            }

            response = await client.PostAsJsonAsync($"https://api.telegram.org/bot{botToken}/sendMessage", payload, ct);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new NotificationDeliveryException("Telegram", (int)response.StatusCode, await DescriptionAsync(response, ct));
        }
    }

    /// <summary>
    /// Every piece is cut to size as raw text and only then HTML-encoded, so a cut can never land
    /// inside an entity like <c>&amp;amp;</c> and leave Telegram a broken one to reject.
    /// The body gives way first; the headline, series and chapter only if the body alone can't.
    /// </summary>
    internal static string Text(NotificationMessage message, int limit)
    {
        var url = Uri.IsWellFormedUriString(message.Url, UriKind.Absolute) ? message.Url : null;

        var text = Fit(cap => Build(message, message.Title, Cut(message.Body, cap), message.SeriesTitle, message.ChapterNumber, url),
            message.Body.Length, limit);
        if (text is not null)
        {
            return text;
        }

        var longest = Math.Max(message.Title.Length, Math.Max(message.SeriesTitle?.Length ?? 0, message.ChapterNumber?.Length ?? 0));
        return Fit(cap => Build(message, Cut(message.Title, cap), string.Empty, Cut(message.SeriesTitle, cap), Cut(message.ChapterNumber, cap), null),
            longest, limit) ?? string.Empty;
    }

    /// <summary>Largest raw cap in [0, max] whose rendering fits, or null when even 0 doesn't.</summary>
    private static string? Fit(Func<int, string> render, int max, int limit)
    {
        var full = render(max);
        if (full.Length <= limit)
        {
            return full;
        }

        int lo = 0, hi = max - 1;
        string? best = null;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var candidate = render(mid);
            if (candidate.Length <= limit)
            {
                best = candidate;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return best;
    }

    private static string Build(NotificationMessage message, string title, string body, string? series, string? chapter, string? url)
    {
        var sb = new StringBuilder();
        sb.Append("<b>").Append(WebUtility.HtmlEncode(title)).Append("</b>\n");
        sb.Append(WebUtility.HtmlEncode(body));

        if (!string.IsNullOrWhiteSpace(series))
        {
            sb.Append('\n').Append(WebUtility.HtmlEncode(series));
        }

        if (!string.IsNullOrWhiteSpace(chapter))
        {
            sb.Append('\n').Append(WebUtility.HtmlEncode($"{message.ChapterLabel} {chapter}"));
        }

        if (url is not null)
        {
            sb.Append('\n').Append(WebUtility.HtmlEncode(url));
        }

        return sb.ToString();
    }

    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(value))]
    private static string? Cut(string? value, int limit) =>
        value is null || value.Length <= limit ? value
        : limit <= 0 ? string.Empty
        : value[..(limit - 1)] + "…";

    /// <summary>Telegram explains a rejection in <c>description</c>; the URL (and its token) never goes near the exception.</summary>
    private static async Task<string?> DescriptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("description", out var d)
                   && d.ValueKind == JsonValueKind.String
                ? Cut(d.GetString(), 300)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
