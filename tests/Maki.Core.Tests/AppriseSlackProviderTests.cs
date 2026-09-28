using System.Net;
using System.Text.Json;
using Maki.Core.Entities;
using Maki.Core.Notifications;

namespace Maki.Core.Tests;

public class AppriseSlackProviderTests
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

    // --- Apprise ---

    [Fact]
    public async Task Apprise_posts_to_the_config_key_route_when_a_config_key_is_set()
    {
        var (provider, handler) = Build(f => new AppriseNotificationProvider(f));
        var connection = new Notification
        {
            Type = NotificationType.Apprise,
            ConfigJson = """{"serverUrl":"https://apprise.example.com","configKey":"myapp"}"""
        };

        await provider.SendAsync(connection, new NotificationMessage(
            NotificationEventType.ChapterDownloaded, "Chapter downloaded", "Naruto — chapter 5",
            SeriesTitle: "Naruto", ChapterNumber: "5"));

        Assert.Equal("https://apprise.example.com/notify/myapp", handler.Request!.RequestUri!.ToString());
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal("Chapter downloaded", payload.RootElement.GetProperty("title").GetString());
        Assert.Contains("Series: Naruto", payload.RootElement.GetProperty("body").GetString());
        Assert.Contains("Chapter: 5", payload.RootElement.GetProperty("body").GetString());
        Assert.Equal("success", payload.RootElement.GetProperty("type").GetString());
        Assert.False(payload.RootElement.TryGetProperty("urls", out _));
    }

    [Fact]
    public async Task Apprise_posts_to_the_urls_route_when_no_config_key_is_set()
    {
        var (provider, handler) = Build(f => new AppriseNotificationProvider(f));
        var connection = new Notification
        {
            Type = NotificationType.Apprise,
            ConfigJson = """{"serverUrl":"https://apprise.example.com","urls":"mailto://a, discord://b"}"""
        };

        await provider.SendAsync(connection, new NotificationMessage(NotificationEventType.Test, "t", "b"));

        Assert.Equal("https://apprise.example.com/notify", handler.Request!.RequestUri!.ToString());
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal("mailto://a, discord://b", payload.RootElement.GetProperty("urls").GetString());
    }

    [Fact]
    public async Task Apprise_throws_when_neither_config_key_nor_urls_are_set()
    {
        var (provider, _) = Build(f => new AppriseNotificationProvider(f));
        var connection = new Notification
        {
            Type = NotificationType.Apprise,
            ConfigJson = """{"serverUrl":"https://apprise.example.com"}"""
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.SendAsync(connection, new NotificationMessage(NotificationEventType.Test, "t", "b")));
    }

    [Theory]
    [InlineData("""{"serverUrl":"https://a.example.com"}""", "error.notifications.appriseTarget")]
    [InlineData("""{"serverUrl":"https://a.example.com","configKey":"  ","urls":""}""", "error.notifications.appriseTarget")]
    [InlineData("""{"serverUrl":"https://a.example.com","configKey":"k"}""", null)]
    [InlineData("""{"serverUrl":"https://a.example.com","urls":"mailto://a"}""", null)]
    public void Apprise_validate_requires_a_config_key_or_urls(string json, string? expected)
    {
        var (provider, _) = Build(f => new AppriseNotificationProvider(f));

        Assert.Equal(expected, provider.Validate(NotificationConfig.Fields(json)));
    }

    [Fact]
    public async Task Apprise_escapes_the_config_key_in_the_path()
    {
        var (provider, handler) = Build(f => new AppriseNotificationProvider(f));
        var connection = new Notification
        {
            Type = NotificationType.Apprise,
            ConfigJson = """{"serverUrl":"https://apprise.example.com","configKey":"a/b c"}"""
        };

        await provider.SendAsync(connection, new NotificationMessage(NotificationEventType.Test, "t", "b"));

        Assert.Equal("/notify/a%2Fb%20c", handler.Request!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public void Apprise_urls_field_is_a_secret()
    {
        var (provider, _) = Build(f => new AppriseNotificationProvider(f));

        Assert.Equal(NotificationFieldKind.Secret, provider.Descriptor.Fields.Single(f => f.Key == "urls").Kind);
    }

    [Theory]
    [InlineData(NotificationLevel.Error, NotificationEventType.Test, "failure")]
    [InlineData(NotificationLevel.Warning, NotificationEventType.Test, "warning")]
    [InlineData(NotificationLevel.Info, NotificationEventType.ChapterDownloaded, "success")]
    [InlineData(NotificationLevel.Info, NotificationEventType.ImportCompleted, "success")]
    [InlineData(NotificationLevel.Info, NotificationEventType.Test, "info")]
    public async Task Apprise_maps_level_and_event_type_to_the_apprise_notify_type(
        NotificationLevel level, NotificationEventType eventType, string expected)
    {
        var (provider, handler) = Build(f => new AppriseNotificationProvider(f));
        var connection = new Notification
        {
            Type = NotificationType.Apprise,
            ConfigJson = """{"serverUrl":"https://apprise.example.com","urls":"mailto://a"}"""
        };

        await provider.SendAsync(connection, new NotificationMessage(eventType, "t", "b", Level: level));

        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal(expected, payload.RootElement.GetProperty("type").GetString());
    }

    // --- Slack ---

    [Fact]
    public async Task Slack_posts_text_with_title_and_body_and_escapes_special_characters()
    {
        var (provider, handler) = Build(f => new SlackWebhookNotificationProvider(f));
        var connection = new Notification
        {
            Type = NotificationType.SlackWebhook,
            ConfigJson = """{"webhookUrl":"https://hooks.slack.com/services/abc"}"""
        };

        await provider.SendAsync(connection, new NotificationMessage(
            NotificationEventType.Test, "A & B", "<script> > done"));

        Assert.Equal("https://hooks.slack.com/services/abc", handler.Request!.RequestUri!.ToString());
        using var payload = JsonDocument.Parse(handler.Body!);
        var text = payload.RootElement.GetProperty("text").GetString();
        Assert.Contains("A &amp; B", text);
        Assert.Contains("&lt;script&gt; &gt; done", text);
    }

    [Fact]
    public async Task Slack_passes_through_the_channel_when_configured()
    {
        var (provider, handler) = Build(f => new SlackWebhookNotificationProvider(f));
        var connection = new Notification
        {
            Type = NotificationType.SlackWebhook,
            ConfigJson = """{"webhookUrl":"https://hooks.slack.com/services/abc","channel":"#manga"}"""
        };

        await provider.SendAsync(connection, new NotificationMessage(NotificationEventType.Test, "t", "b"));

        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal("#manga", payload.RootElement.GetProperty("channel").GetString());
    }

    [Fact]
    public async Task Slack_omits_attachments_when_there_is_no_series_or_chapter()
    {
        var (provider, handler) = Build(f => new SlackWebhookNotificationProvider(f));
        var connection = new Notification
        {
            Type = NotificationType.SlackWebhook,
            ConfigJson = """{"webhookUrl":"https://hooks.slack.com/services/abc"}"""
        };

        await provider.SendAsync(connection, new NotificationMessage(NotificationEventType.Test, "t", "b"));

        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.False(payload.RootElement.TryGetProperty("attachments", out _));
    }

    [Fact]
    public async Task Slack_adds_an_attachment_with_fields_and_color_when_series_or_chapter_is_present()
    {
        var (provider, handler) = Build(f => new SlackWebhookNotificationProvider(f));
        var connection = new Notification
        {
            Type = NotificationType.SlackWebhook,
            ConfigJson = """{"webhookUrl":"https://hooks.slack.com/services/abc"}"""
        };

        await provider.SendAsync(connection, new NotificationMessage(
            NotificationEventType.ChapterDownloaded, "Chapter downloaded", "b",
            SeriesTitle: "Naruto", ChapterNumber: "5"));

        using var payload = JsonDocument.Parse(handler.Body!);
        var attachment = payload.RootElement.GetProperty("attachments")[0];
        Assert.Equal("#57f287", attachment.GetProperty("color").GetString());
        var fields = attachment.GetProperty("fields");
        Assert.Equal(2, fields.GetArrayLength());
        Assert.Equal("Series", fields[0].GetProperty("title").GetString());
        Assert.Equal("Naruto", fields[0].GetProperty("value").GetString());
        Assert.Equal("Chapter", fields[1].GetProperty("title").GetString());
        Assert.Equal("5", fields[1].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Slack_escapes_attachment_fields_and_titles_the_attachment_with_the_series()
    {
        var (provider, handler) = Build(f => new SlackWebhookNotificationProvider(f));
        var connection = new Notification
        {
            Type = NotificationType.SlackWebhook,
            ConfigJson = """{"webhookUrl":"https://hooks.slack.com/services/abc"}"""
        };

        await provider.SendAsync(connection, new NotificationMessage(
            NotificationEventType.ChapterDownloaded, "t", "b",
            SeriesTitle: "Tom & <Jerry>", ChapterNumber: "5<6", SeriesLabel: "Serie", ChapterLabel: "Kapitel",
            Url: "https://maki.example.com/series/12"));

        using var payload = JsonDocument.Parse(handler.Body!);
        var attachment = payload.RootElement.GetProperty("attachments")[0];
        Assert.Equal("Tom &amp; &lt;Jerry&gt;", attachment.GetProperty("title").GetString());
        Assert.Equal("https://maki.example.com/series/12", attachment.GetProperty("title_link").GetString());
        var fields = attachment.GetProperty("fields");
        Assert.Equal("Serie", fields[0].GetProperty("title").GetString());
        Assert.Equal("Tom &amp; &lt;Jerry&gt;", fields[0].GetProperty("value").GetString());
        Assert.Equal("Kapitel", fields[1].GetProperty("title").GetString());
        Assert.Equal("5&lt;6", fields[1].GetProperty("value").GetString());
        Assert.StartsWith("*t*", payload.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Slack_links_the_headline_when_there_is_no_series()
    {
        var (provider, handler) = Build(f => new SlackWebhookNotificationProvider(f));
        var connection = new Notification
        {
            Type = NotificationType.SlackWebhook,
            ConfigJson = """{"webhookUrl":"https://hooks.slack.com/services/abc"}"""
        };

        await provider.SendAsync(connection, new NotificationMessage(
            NotificationEventType.UpdateAvailable, "A & B", "b", Url: "https://maki.example.com/settings"));
        using (var payload = JsonDocument.Parse(handler.Body!))
        {
            Assert.StartsWith("*<https://maki.example.com/settings|A &amp; B>*",
                payload.RootElement.GetProperty("text").GetString());
        }

        await provider.SendAsync(connection, new NotificationMessage(
            NotificationEventType.UpdateAvailable, "A & B", "b", Url: "/settings"));
        using (var payload = JsonDocument.Parse(handler.Body!))
        {
            Assert.StartsWith("*A &amp; B*", payload.RootElement.GetProperty("text").GetString());
        }
    }

    [Fact]
    public async Task Slack_non_success_status_throws_delivery_exception()
    {
        var (provider, _) = Build(f => new SlackWebhookNotificationProvider(f), HttpStatusCode.NotFound);
        var connection = new Notification
        {
            Type = NotificationType.SlackWebhook,
            ConfigJson = """{"webhookUrl":"https://hooks.slack.com/services/secret"}"""
        };

        var ex = await Assert.ThrowsAsync<NotificationDeliveryException>(() =>
            provider.SendAsync(connection, new NotificationMessage(NotificationEventType.Test, "t", "b")));

        Assert.Equal(404, ex.StatusCode);
        Assert.DoesNotContain("secret", ex.Message);
    }

    [Fact]
    public async Task Slack_includes_title_link_only_when_the_url_is_absolute()
    {
        var (provider, handler) = Build(f => new SlackWebhookNotificationProvider(f));
        var connection = new Notification
        {
            Type = NotificationType.SlackWebhook,
            ConfigJson = """{"webhookUrl":"https://hooks.slack.com/services/abc"}"""
        };

        await provider.SendAsync(connection, new NotificationMessage(
            NotificationEventType.ChapterDownloaded, "t", "b", SeriesTitle: "Naruto", Url: "/series/12"));

        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.False(payload.RootElement.GetProperty("attachments")[0].TryGetProperty("title_link", out _));

        await provider.SendAsync(connection, new NotificationMessage(
            NotificationEventType.ChapterDownloaded, "t", "b", SeriesTitle: "Naruto",
            Url: "https://maki.example.com/series/12"));

        using var payload2 = JsonDocument.Parse(handler.Body!);
        Assert.Equal(
            "https://maki.example.com/series/12",
            payload2.RootElement.GetProperty("attachments")[0].GetProperty("title_link").GetString());
    }
}
