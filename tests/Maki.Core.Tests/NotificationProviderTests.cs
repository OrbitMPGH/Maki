using System.Net;
using System.Text.Json;
using Maki.Core.Entities;
using Maki.Core.Notifications;

namespace Maki.Core.Tests;

public class NotificationProviderTests
{
    private sealed class CapturingHandler(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
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

            return new HttpResponseMessage(status);
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static (T provider, CapturingHandler handler) Build<T>(
        Func<IHttpClientFactory, T> ctor, HttpStatusCode status = HttpStatusCode.OK)
        where T : INotificationProvider
    {
        var handler = new CapturingHandler(status);
        var factory = new SingleClientFactory(new HttpClient(handler));
        return (ctor(factory), handler);
    }

    private sealed class StubCoverStore(string? path) : INotificationCoverStore
    {
        public string? PosterPathFor(int seriesId) => path;
    }

    private static Notification DiscordConnection() => new()
    {
        Type = NotificationType.Discord,
        ConfigJson = """{"webhookUrl":"https://discord.com/api/webhooks/abc"}"""
    };

    [Fact]
    public async Task Discord_posts_an_embed_to_the_webhook_url()
    {
        var (provider, handler) = Build(f => new DiscordNotificationProvider(f));
        var message = new NotificationMessage(
            NotificationEventType.ChapterDownloaded, "Chapter downloaded", "Naruto — chapter 5",
            SeriesTitle: "Naruto", ChapterNumber: "5");

        await provider.SendAsync(DiscordConnection(), message);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://discord.com/api/webhooks/abc", handler.Request.RequestUri!.ToString());
        Assert.Contains("embeds", handler.Body);
        Assert.Contains("Chapter downloaded", handler.Body);
        Assert.Contains("Naruto", handler.Body);
    }

    [Fact]
    public async Task Discord_uses_the_series_as_the_title_and_drops_it_from_the_description()
    {
        var (provider, handler) = Build(f => new DiscordNotificationProvider(f));
        var message = new NotificationMessage(
            NotificationEventType.ChapterDownloaded, "Chapter downloaded", "Naruto — chapter 5",
            SeriesTitle: "Naruto", ChapterNumber: "5");

        await provider.SendAsync(DiscordConnection(), message);

        using var payload = JsonDocument.Parse(handler.Body!);
        var embed = payload.RootElement.GetProperty("embeds")[0];
        Assert.Equal("Naruto", embed.GetProperty("title").GetString());
        Assert.Equal("chapter 5", embed.GetProperty("description").GetString());
        Assert.Contains("Chapter downloaded", embed.GetProperty("author").GetProperty("name").GetString());
        Assert.Equal("5", embed.GetProperty("fields")[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Discord_colors_by_event_but_a_failure_level_wins()
    {
        var (provider, handler) = Build(f => new DiscordNotificationProvider(f));

        await provider.SendAsync(DiscordConnection(), new NotificationMessage(
            NotificationEventType.ChapterDownloaded, "Chapter downloaded", "b"));
        var downloaded = ColorOf(handler.Body!);

        await provider.SendAsync(DiscordConnection(), new NotificationMessage(
            NotificationEventType.ChapterDownloaded, "Chapter downloaded", "b",
            Level: NotificationLevel.Error));

        Assert.NotEqual(downloaded, ColorOf(handler.Body!));
        Assert.Equal(0xED4245, ColorOf(handler.Body!));

        static int ColorOf(string body)
        {
            using var payload = JsonDocument.Parse(body);
            return payload.RootElement.GetProperty("embeds")[0].GetProperty("color").GetInt32();
        }
    }

    [Fact]
    public async Task Discord_uploads_the_poster_and_references_it_as_an_attachment()
    {
        var poster = Path.Combine(Path.GetTempPath(), $"maki-poster-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(poster, [0xFF, 0xD8, 0xFF, 0xD9]);
        try
        {
            var (provider, handler) = Build(f => new DiscordNotificationProvider(f, new StubCoverStore(poster)));

            await provider.SendAsync(DiscordConnection(), new NotificationMessage(
                NotificationEventType.ChapterDownloaded, "Chapter downloaded", "Naruto — chapter 5",
                SeriesTitle: "Naruto", SeriesId: 7));

            Assert.Equal("multipart/form-data", handler.Request!.Content!.Headers.ContentType!.MediaType);
            Assert.Contains("payload_json", handler.Body);
            Assert.Contains("attachment://poster.jpg", handler.Body);
            Assert.Contains("files[0]", handler.Body);
        }
        finally
        {
            File.Delete(poster);
        }
    }

    [Fact]
    public async Task Discord_sends_plain_json_when_the_series_has_no_poster()
    {
        var (provider, handler) = Build(f => new DiscordNotificationProvider(f, new StubCoverStore(null)));

        await provider.SendAsync(DiscordConnection(), new NotificationMessage(
            NotificationEventType.ChapterDownloaded, "Chapter downloaded", "b", SeriesId: 7));

        Assert.Equal("application/json", handler.Request!.Content!.Headers.ContentType!.MediaType);
        Assert.DoesNotContain("attachment://", handler.Body);
    }

    [Fact]
    public async Task Discord_truncates_a_body_past_the_embed_description_limit()
    {
        var (provider, handler) = Build(f => new DiscordNotificationProvider(f));

        await provider.SendAsync(DiscordConnection(), new NotificationMessage(
            NotificationEventType.DownloadFailed, "Download failed", new string('x', 5000)));

        using var payload = JsonDocument.Parse(handler.Body!);
        var description = payload.RootElement.GetProperty("embeds")[0].GetProperty("description").GetString();
        Assert.Equal(4096, description!.Length);
    }

    [Fact]
    public async Task Discord_throws_when_webhook_url_missing()
    {
        var (provider, _) = Build(f => new DiscordNotificationProvider(f));
        var connection = new Notification { Type = NotificationType.Discord, ConfigJson = "{}" };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.SendAsync(connection, new NotificationMessage(NotificationEventType.Test, "t", "b")));
    }

    [Fact]
    public async Task Webhook_posts_json_with_optional_bearer_token()
    {
        var (provider, handler) = Build(f => new WebhookNotificationProvider(f));
        var connection = new Notification
        {
            Type = NotificationType.Webhook,
            ConfigJson = """{"url":"https://example.com/hook","bearerToken":"secret123"}"""
        };
        var message = new NotificationMessage(NotificationEventType.DownloadFailed, "Download failed", "boom");

        await provider.SendAsync(connection, message);

        Assert.Equal("https://example.com/hook", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Equal("secret123", handler.Request.Headers.Authorization.Parameter);
        Assert.Contains("DownloadFailed", handler.Body);
        Assert.Contains("boom", handler.Body);
    }

    [Fact]
    public async Task Webhook_without_token_sends_no_authorization_header()
    {
        var (provider, handler) = Build(f => new WebhookNotificationProvider(f));
        var connection = new Notification
        {
            Type = NotificationType.Webhook,
            ConfigJson = """{"url":"https://example.com/hook"}"""
        };

        await provider.SendAsync(connection, new NotificationMessage(NotificationEventType.Test, "t", "b"));

        Assert.Null(handler.Request!.Headers.Authorization);
    }

    [Fact]
    public async Task Non_success_status_throws()
    {
        var (provider, _) = Build(f => new WebhookNotificationProvider(f), HttpStatusCode.InternalServerError);
        var connection = new Notification
        {
            Type = NotificationType.Webhook,
            ConfigJson = """{"url":"https://example.com/hook"}"""
        };

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.SendAsync(connection, new NotificationMessage(NotificationEventType.Test, "t", "b")));
    }
}
