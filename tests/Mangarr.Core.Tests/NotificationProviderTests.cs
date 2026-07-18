using System.Net;
using Mangarr.Core.Entities;
using Mangarr.Core.Notifications;

namespace Mangarr.Core.Tests;

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

    [Fact]
    public async Task Discord_posts_an_embed_to_the_webhook_url()
    {
        var (provider, handler) = Build(f => new DiscordNotificationProvider(f));
        var connection = new Notification
        {
            Type = NotificationType.Discord,
            ConfigJson = """{"webhookUrl":"https://discord.com/api/webhooks/abc"}"""
        };
        var message = new NotificationMessage(
            NotificationEventType.ChapterDownloaded, "Chapter downloaded", "Naruto — chapter 5",
            SeriesTitle: "Naruto", ChapterNumber: "5");

        await provider.SendAsync(connection, message);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://discord.com/api/webhooks/abc", handler.Request.RequestUri!.ToString());
        Assert.Contains("embeds", handler.Body);
        Assert.Contains("Chapter downloaded", handler.Body);
        Assert.Contains("Naruto", handler.Body);
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
