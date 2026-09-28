using System.Net;
using System.Text.Json;
using Maki.Core.Entities;
using Maki.Core.Notifications;

namespace Maki.Core.Tests;

public class TelegramNotifiarrProviderTests
{
    private sealed class CapturingHandler(HttpStatusCode status = HttpStatusCode.OK, string? responseBody = null) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            if (request.Content is not null)
            {
                Body = await request.Content.ReadAsStringAsync(ct);
            }

            var response = new HttpResponseMessage(status);
            if (responseBody is not null)
            {
                response.Content = new StringContent(responseBody);
            }

            return response;
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubCoverStore(string? path) : INotificationCoverStore
    {
        public string? PosterPathFor(int seriesId) => path;
    }

    private static (T provider, CapturingHandler handler) Build<T>(
        Func<IHttpClientFactory, T> ctor, HttpStatusCode status = HttpStatusCode.OK, string? responseBody = null)
    {
        var handler = new CapturingHandler(status, responseBody);
        var factory = new SingleClientFactory(new HttpClient(handler));
        return (ctor(factory), handler);
    }

    private static Notification TelegramConnection(string extra = "") => new()
    {
        Type = NotificationType.Telegram,
        ConfigJson = "{\"botToken\":\"123:ABC\",\"chatId\":\"456\"" + extra + "}"
    };

    [Fact]
    public async Task Telegram_sends_json_message_with_html_escaping()
    {
        var (provider, handler) = Build(f => new TelegramNotificationProvider(f));
        var message = new NotificationMessage(
            NotificationEventType.ChapterDownloaded, "New <chapter>", "Body & text",
            SeriesTitle: "Naruto", ChapterNumber: "5");

        await provider.SendAsync(TelegramConnection(), message);

        Assert.Equal("https://api.telegram.org/bot123:ABC/sendMessage", handler.Request!.RequestUri!.ToString());
        using var payload = JsonDocument.Parse(handler.Body!);
        var root = payload.RootElement;
        Assert.Equal("456", root.GetProperty("chat_id").GetString());
        Assert.Equal("HTML", root.GetProperty("parse_mode").GetString());
        Assert.True(root.GetProperty("disable_web_page_preview").GetBoolean());
        var text = root.GetProperty("text").GetString();
        Assert.Contains("&lt;chapter&gt;", text);
        Assert.Contains("Body &amp; text", text);
        Assert.Contains("Chapter 5", text);
        Assert.False(root.TryGetProperty("disable_notification", out _));
        Assert.False(root.TryGetProperty("message_thread_id", out _));
    }

    [Fact]
    public async Task Telegram_includes_thread_id_and_silent_flag()
    {
        var (provider, handler) = Build(f => new TelegramNotificationProvider(f));
        var connection = TelegramConnection(",\"threadId\":\"99\",\"silent\":\"true\"");

        await provider.SendAsync(connection, new NotificationMessage(NotificationEventType.Test, "t", "b"));

        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal("99", payload.RootElement.GetProperty("message_thread_id").GetString());
        Assert.True(payload.RootElement.GetProperty("disable_notification").GetBoolean());
    }

    [Fact]
    public async Task Telegram_sends_multipart_photo_when_poster_available()
    {
        var poster = Path.Combine(Path.GetTempPath(), $"maki-poster-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(poster, [0xFF, 0xD8, 0xFF, 0xD9]);
        try
        {
            var (provider, handler) = Build(f => new TelegramNotificationProvider(f, new StubCoverStore(poster)));

            await provider.SendAsync(TelegramConnection(), new NotificationMessage(
                NotificationEventType.ChapterDownloaded, "Chapter downloaded", "Naruto — chapter 5",
                SeriesTitle: "Naruto", SeriesId: 7));

            Assert.Equal("https://api.telegram.org/bot123:ABC/sendPhoto", handler.Request!.RequestUri!.ToString());
            Assert.Equal("multipart/form-data", handler.Request.Content!.Headers.ContentType!.MediaType);
            Assert.Contains("Content-Disposition: form-data; name=photo", handler.Body!.Replace("\"", ""));
            Assert.Contains("caption", handler.Body);
        }
        finally
        {
            File.Delete(poster);
        }
    }

    [Fact]
    public async Task Telegram_throws_when_bot_token_missing()
    {
        var (provider, _) = Build(f => new TelegramNotificationProvider(f));
        var connection = new Notification { Type = NotificationType.Telegram, ConfigJson = """{"chatId":"1"}""" };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.SendAsync(connection, new NotificationMessage(NotificationEventType.Test, "t", "b")));
    }

    [Fact]
    public async Task Telegram_failure_status_throws_without_token_in_message()
    {
        var (provider, _) = Build(f => new TelegramNotificationProvider(f), HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<NotificationDeliveryException>(() =>
            provider.SendAsync(TelegramConnection(), new NotificationMessage(NotificationEventType.Test, "t", "b")));

        Assert.DoesNotContain("123:ABC", ex.Message);
        Assert.Contains("Telegram returned", ex.Message);
    }

    [Fact]
    public async Task Telegram_failure_carries_telegram_description_as_detail()
    {
        var (provider, _) = Build(f => new TelegramNotificationProvider(f), HttpStatusCode.BadRequest,
            """{"ok":false,"error_code":400,"description":"Bad Request: chat not found"}""");

        var ex = await Assert.ThrowsAsync<NotificationDeliveryException>(() =>
            provider.SendAsync(TelegramConnection(), new NotificationMessage(NotificationEventType.Test, "t", "b")));

        Assert.Equal("Telegram", ex.Provider);
        Assert.Equal(400, ex.StatusCode);
        Assert.Equal("Bad Request: chat not found", ex.Detail);
        Assert.Contains("chat not found", ex.Message);
        Assert.DoesNotContain("123:ABC", ex.Message);
        Assert.DoesNotContain("api.telegram.org", ex.Message);
    }

    [Fact]
    public async Task Telegram_escapes_ampersands_in_the_url()
    {
        var (provider, handler) = Build(f => new TelegramNotificationProvider(f));

        await provider.SendAsync(TelegramConnection(), new NotificationMessage(
            NotificationEventType.Test, "t", "b", Url: "https://maki.example.com/series?id=1&tab=2"));

        using var payload = JsonDocument.Parse(handler.Body!);
        var text = payload.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("https://maki.example.com/series?id=1&amp;tab=2", text);
        Assert.DoesNotContain("id=1&tab", text);
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(4096)]
    public void Telegram_truncation_never_splits_an_entity(int limit)
    {
        // Every '&' encodes to five characters, so a cut after encoding would land mid-entity for most limits.
        for (var pad = 0; pad < 6; pad++)
        {
            var body = new string('x', pad) + string.Concat(Enumerable.Repeat("&", limit));
            var text = TelegramNotificationProvider.Text(
                new NotificationMessage(NotificationEventType.Test, "Title", body, SeriesTitle: "S", ChapterNumber: "5"),
                limit);

            Assert.True(text.Length <= limit);
            Assert.EndsWith("…\nS\nChapter 5", text);
            var bodyPart = text["<b>Title</b>\n".Length..text.IndexOf('…')];
            Assert.Matches("^x*(&amp;)*$", bodyPart);
        }
    }

    [Fact]
    public void Telegram_shrinks_the_headline_when_the_body_alone_cannot_make_room()
    {
        var text = TelegramNotificationProvider.Text(
            new NotificationMessage(NotificationEventType.Test, new string('<', 2000), "b"), 1024);

        Assert.True(text.Length <= 1024);
        Assert.StartsWith("<b>&lt;", text);
        Assert.Matches("^<b>(&lt;)*…</b>\n$", text);
    }

    [Fact]
    public async Task Telegram_uses_the_localized_chapter_label()
    {
        var (provider, handler) = Build(f => new TelegramNotificationProvider(f));

        await provider.SendAsync(TelegramConnection(), new NotificationMessage(
            NotificationEventType.ChapterDownloaded, "t", "b", ChapterNumber: "5", ChapterLabel: "Kapitel"));

        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Contains("Kapitel 5", payload.RootElement.GetProperty("text").GetString());
    }

    private static Notification NotifiarrConnection() => new()
    {
        Type = NotificationType.Notifiarr,
        ConfigJson = """{"apiKey":"apikey123","channelId":"789"}"""
    };

    [Fact]
    public async Task Notifiarr_posts_expected_payload_shape_and_colour()
    {
        var (provider, handler) = Build(f => new NotifiarrNotificationProvider(f));
        var message = new NotificationMessage(
            NotificationEventType.ChapterDownloaded, "Chapter downloaded", "Naruto — chapter 5",
            SeriesTitle: "Naruto", ChapterNumber: "5");

        await provider.SendAsync(NotifiarrConnection(), message);

        Assert.Equal(
            "https://notifiarr.com/api/v1/notification/passthrough/apikey123",
            handler.Request!.RequestUri!.ToString());

        using var payload = JsonDocument.Parse(handler.Body!);
        var root = payload.RootElement;
        Assert.Equal("Maki", root.GetProperty("notification").GetProperty("name").GetString());
        Assert.Equal("ChapterDownloaded", root.GetProperty("notification").GetProperty("event").GetString());
        Assert.False(root.GetProperty("notification").GetProperty("update").GetBoolean());

        var discord = root.GetProperty("discord");
        Assert.Equal("57F287", discord.GetProperty("color").GetString());
        Assert.Equal("Chapter downloaded", discord.GetProperty("text").GetProperty("title").GetString());
        Assert.Equal("Naruto — chapter 5", discord.GetProperty("text").GetProperty("description").GetString());
        Assert.Equal("Maki", discord.GetProperty("text").GetProperty("footer").GetString());
        Assert.Equal(789, discord.GetProperty("ids").GetProperty("channel").GetInt64());

        var fields = discord.GetProperty("text").GetProperty("fields");
        Assert.Equal(2, fields.GetArrayLength());
        Assert.Equal("Series", fields[0].GetProperty("title").GetString());
        Assert.Equal("Naruto", fields[0].GetProperty("text").GetString());
        Assert.Equal("Chapter", fields[1].GetProperty("title").GetString());
    }

    [Fact]
    public async Task Notifiarr_throws_when_channel_id_is_not_numeric()
    {
        var (provider, _) = Build(f => new NotifiarrNotificationProvider(f));
        var connection = new Notification
        {
            Type = NotificationType.Notifiarr,
            ConfigJson = """{"apiKey":"key","channelId":"not-a-number"}"""
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.SendAsync(connection, new NotificationMessage(NotificationEventType.Test, "t", "b")));
    }

    [Fact]
    public async Task Notifiarr_failure_status_throws_without_key_in_message()
    {
        var (provider, _) = Build(f => new NotifiarrNotificationProvider(f), HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<NotificationDeliveryException>(() =>
            provider.SendAsync(NotifiarrConnection(), new NotificationMessage(NotificationEventType.Test, "t", "b")));

        Assert.DoesNotContain("apikey123", ex.Message);
        Assert.Contains("Notifiarr returned", ex.Message);
    }
}
