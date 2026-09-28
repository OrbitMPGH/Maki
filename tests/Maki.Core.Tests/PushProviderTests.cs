using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Maki.Core.Entities;
using Maki.Core.Notifications;

namespace Maki.Core.Tests;

public class PushProviderTests
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

    private sealed class StubCoverStore(string? path) : INotificationCoverStore
    {
        public string? PosterPathFor(int seriesId) => path;
    }

    private static (T provider, CapturingHandler handler) Build<T>(
        Func<IHttpClientFactory, T> ctor, HttpStatusCode status = HttpStatusCode.OK)
        where T : INotificationProvider
    {
        var handler = new CapturingHandler(status);
        var factory = new SingleClientFactory(new HttpClient(handler));
        return (ctor(factory), handler);
    }

    // ---- Ntfy ----

    private static Notification NtfyConnection(string extra = "") => new()
    {
        Type = NotificationType.Ntfy,
        ConfigJson = $$"""{"serverUrl":"https://ntfy.sh","topic":"maki-alerts"{{extra}}}"""
    };

    [Fact]
    public async Task Ntfy_posts_json_to_the_server_root_with_topic()
    {
        var (provider, handler) = Build(f => new NtfyNotificationProvider(f));

        await provider.SendAsync(NtfyConnection(), new NotificationMessage(
            NotificationEventType.ChapterDownloaded, "Chapter downloaded", "Naruto ch. 5",
            SeriesTitle: "Naruto", ChapterNumber: "5"));

        Assert.Equal("https://ntfy.sh/", handler.Request!.RequestUri!.ToString());
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal("maki-alerts", payload.RootElement.GetProperty("topic").GetString());
        Assert.Equal("Chapter downloaded", payload.RootElement.GetProperty("title").GetString());
        Assert.Contains("Series: Naruto", payload.RootElement.GetProperty("message").GetString());
        Assert.Contains("Chapter: 5", payload.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Ntfy_trims_trailing_slash_from_server_url()
    {
        var (provider, handler) = Build(f => new NtfyNotificationProvider(f));
        var connection = new Notification
        {
            Type = NotificationType.Ntfy,
            ConfigJson = """{"serverUrl":"https://ntfy.sh/","topic":"t"}"""
        };

        await provider.SendAsync(connection, new NotificationMessage(NotificationEventType.Test, "t", "b"));

        Assert.Equal("https://ntfy.sh/", handler.Request!.RequestUri!.ToString());
    }

    [Fact]
    public async Task Ntfy_defaults_priority_to_three_and_bumps_to_four_on_error()
    {
        var (provider, handler) = Build(f => new NtfyNotificationProvider(f));

        await provider.SendAsync(NtfyConnection(), new NotificationMessage(NotificationEventType.Test, "t", "b"));
        using (var payload = JsonDocument.Parse(handler.Body!))
        {
            Assert.Equal(3, payload.RootElement.GetProperty("priority").GetInt32());
        }

        await provider.SendAsync(NtfyConnection(), new NotificationMessage(
            NotificationEventType.Test, "t", "b", Level: NotificationLevel.Error));
        using (var payload = JsonDocument.Parse(handler.Body!))
        {
            Assert.Equal(4, payload.RootElement.GetProperty("priority").GetInt32());
        }
    }

    [Fact]
    public async Task Ntfy_respects_explicit_priority_over_the_error_default()
    {
        var (provider, handler) = Build(f => new NtfyNotificationProvider(f));

        await provider.SendAsync(
            NtfyConnection(",\"priority\":1"),
            new NotificationMessage(NotificationEventType.Test, "t", "b", Level: NotificationLevel.Error));

        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal(1, payload.RootElement.GetProperty("priority").GetInt32());
    }

    [Fact]
    public async Task Ntfy_sends_bearer_token_when_configured()
    {
        var (provider, handler) = Build(f => new NtfyNotificationProvider(f));

        await provider.SendAsync(
            NtfyConnection(",\"token\":\"secret\""),
            new NotificationMessage(NotificationEventType.Test, "t", "b"));

        Assert.Equal("Bearer", handler.Request!.Headers.Authorization!.Scheme);
        Assert.Equal("secret", handler.Request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task Ntfy_includes_click_only_for_absolute_urls()
    {
        var (provider, handler) = Build(f => new NtfyNotificationProvider(f));

        await provider.SendAsync(NtfyConnection(), new NotificationMessage(
            NotificationEventType.Test, "t", "b", Url: "/series/12"));
        using (var payload = JsonDocument.Parse(handler.Body!))
        {
            Assert.False(payload.RootElement.TryGetProperty("click", out _));
        }

        await provider.SendAsync(NtfyConnection(), new NotificationMessage(
            NotificationEventType.Test, "t", "b", Url: "https://maki.example.com/series/12"));
        using (var payload = JsonDocument.Parse(handler.Body!))
        {
            Assert.Equal("https://maki.example.com/series/12", payload.RootElement.GetProperty("click").GetString());
        }
    }

    [Theory]
    [InlineData(NotificationEventType.ChapterDownloaded, "inbox_tray")]
    [InlineData(NotificationEventType.NewChapterAvailable, "new")]
    [InlineData(NotificationEventType.ImportCompleted, "package")]
    [InlineData(NotificationEventType.UpdateAvailable, "rocket")]
    [InlineData(NotificationEventType.Test, "bell")]
    public async Task Ntfy_maps_event_type_to_tag(NotificationEventType eventType, string tag)
    {
        var (provider, handler) = Build(f => new NtfyNotificationProvider(f));

        await provider.SendAsync(NtfyConnection(), new NotificationMessage(eventType, "t", "b"));

        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal(tag, payload.RootElement.GetProperty("tags")[0].GetString());
    }

    [Fact]
    public async Task Ntfy_error_level_tag_wins_over_event_type()
    {
        var (provider, handler) = Build(f => new NtfyNotificationProvider(f));

        await provider.SendAsync(NtfyConnection(), new NotificationMessage(
            NotificationEventType.ChapterDownloaded, "t", "b", Level: NotificationLevel.Error));

        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal("x", payload.RootElement.GetProperty("tags")[0].GetString());
    }

    [Fact]
    public async Task Ntfy_throws_when_required_fields_missing()
    {
        var (provider, _) = Build(f => new NtfyNotificationProvider(f));
        var connection = new Notification { Type = NotificationType.Ntfy, ConfigJson = "{}" };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.SendAsync(connection, new NotificationMessage(NotificationEventType.Test, "t", "b")));
    }

    [Fact]
    public async Task Ntfy_non_success_status_throws_without_leaking_token()
    {
        var (provider, _) = Build(f => new NtfyNotificationProvider(f), HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<NotificationDeliveryException>(() =>
            provider.SendAsync(
                NtfyConnection(",\"token\":\"super-secret-token\""),
                new NotificationMessage(NotificationEventType.Test, "t", "b")));

        Assert.DoesNotContain("super-secret-token", ex.Message);
        Assert.Contains("ntfy", ex.Message);
        Assert.Contains("401", ex.Message);
    }

    // ---- Gotify ----

    private static Notification GotifyConnection(string extra = "") => new()
    {
        Type = NotificationType.Gotify,
        ConfigJson = $$"""{"serverUrl":"https://gotify.example.com","appToken":"tok123"{{extra}}}"""
    };

    [Fact]
    public async Task Gotify_posts_json_to_message_endpoint_with_key_header()
    {
        var (provider, handler) = Build(f => new GotifyNotificationProvider(f));

        await provider.SendAsync(GotifyConnection(), new NotificationMessage(
            NotificationEventType.ChapterDownloaded, "Chapter downloaded", "body text",
            SeriesTitle: "Naruto", ChapterNumber: "5"));

        Assert.Equal("https://gotify.example.com/message", handler.Request!.RequestUri!.ToString());
        Assert.Equal("tok123", handler.Request.Headers.GetValues("X-Gotify-Key").Single());

        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal("Chapter downloaded", payload.RootElement.GetProperty("title").GetString());
        Assert.Contains("Series: Naruto", payload.RootElement.GetProperty("message").GetString());
        Assert.Contains("Chapter: 5", payload.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Gotify_defaults_priority_to_five_and_eight_on_error()
    {
        var (provider, handler) = Build(f => new GotifyNotificationProvider(f));

        await provider.SendAsync(GotifyConnection(), new NotificationMessage(NotificationEventType.Test, "t", "b"));
        using (var payload = JsonDocument.Parse(handler.Body!))
        {
            Assert.Equal(5, payload.RootElement.GetProperty("priority").GetInt32());
        }

        await provider.SendAsync(GotifyConnection(), new NotificationMessage(
            NotificationEventType.Test, "t", "b", Level: NotificationLevel.Error));
        using (var payload = JsonDocument.Parse(handler.Body!))
        {
            Assert.Equal(8, payload.RootElement.GetProperty("priority").GetInt32());
        }
    }

    [Fact]
    public async Task Gotify_respects_explicit_priority()
    {
        var (provider, handler) = Build(f => new GotifyNotificationProvider(f));

        await provider.SendAsync(
            GotifyConnection(",\"priority\":2"),
            new NotificationMessage(NotificationEventType.Test, "t", "b", Level: NotificationLevel.Error));

        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal(2, payload.RootElement.GetProperty("priority").GetInt32());
    }

    [Fact]
    public async Task Gotify_throws_when_required_fields_missing()
    {
        var (provider, _) = Build(f => new GotifyNotificationProvider(f));
        var connection = new Notification { Type = NotificationType.Gotify, ConfigJson = """{"serverUrl":"https://g.example.com"}""" };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.SendAsync(connection, new NotificationMessage(NotificationEventType.Test, "t", "b")));
    }

    [Fact]
    public async Task Gotify_non_success_status_throws_without_leaking_token()
    {
        var (provider, _) = Build(f => new GotifyNotificationProvider(f), HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<NotificationDeliveryException>(() =>
            provider.SendAsync(GotifyConnection(), new NotificationMessage(NotificationEventType.Test, "t", "b")));

        Assert.DoesNotContain("tok123", ex.Message);
        Assert.Contains("Gotify", ex.Message);
        Assert.Contains("403", ex.Message);
    }

    // ---- Pushover ----

    private static Notification PushoverConnection(string extra = "") => new()
    {
        Type = NotificationType.Pushover,
        ConfigJson = $$"""{"appToken":"app123","userKey":"user456"{{extra}}}"""
    };

    /// <summary>
    /// Reads a form-data part's value out of the raw multipart body text. The provider disposes its
    /// <c>MultipartFormDataContent</c> once <c>SendAsync</c> returns, so by the time a test inspects
    /// the request the live content tree is gone; <see cref="CapturingHandler"/> already captured the
    /// serialized body while the content was still alive, so tests read from that string instead.
    /// </summary>
    /// <summary>Splits the multipart body into per-field (headers, value) chunks on its boundary line.</summary>
    private static IEnumerable<(string headers, string value)> Parts(CapturingHandler handler)
    {
        var body = handler.Body!;
        var boundary = "--" + handler.Request!.Content!.Headers.ContentType!.Parameters
            .Single(p => p.Name == "boundary").Value!.Trim('"');

        foreach (var chunk in body.Split(boundary, StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = chunk.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            yield return (chunk[..separator], chunk[(separator + 4)..].TrimEnd('\r', '\n', '-'));
        }
    }

    private static string? FieldOf(CapturingHandler handler, string name) =>
        Parts(handler).FirstOrDefault(p => Regex.IsMatch(p.headers, $"name={Regex.Escape(name)}(;|$)")).value;

    private static bool HasPart(CapturingHandler handler, string name) =>
        Parts(handler).Any(p => Regex.IsMatch(p.headers, $"name={Regex.Escape(name)}(;|$)"));

    private const string Endpoint = "https://api.pushover.net/1/messages.json";

    [Fact]
    public async Task Pushover_posts_multipart_form_with_token_and_user()
    {
        var (provider, handler) = Build(f => new PushoverNotificationProvider(f));

        await provider.SendAsync(PushoverConnection(), new NotificationMessage(
            NotificationEventType.ChapterDownloaded, "Chapter downloaded", "body text",
            SeriesTitle: "Naruto", ChapterNumber: "5"));

        Assert.Equal(Endpoint, handler.Request!.RequestUri!.ToString());
        Assert.Equal("app123", FieldOf(handler, "token"));
        Assert.Equal("user456", FieldOf(handler, "user"));
        Assert.Equal("Chapter downloaded", FieldOf(handler, "title"));

        var message = FieldOf(handler, "message");
        Assert.Contains("Series: Naruto", message);
        Assert.Contains("Chapter: 5", message);
    }

    [Fact]
    public async Task Pushover_omits_priority_field_when_unset()
    {
        var (provider, handler) = Build(f => new PushoverNotificationProvider(f));

        await provider.SendAsync(PushoverConnection(), new NotificationMessage(NotificationEventType.Test, "t", "b"));

        Assert.False(HasPart(handler, "priority"));
    }

    [Fact]
    public async Task Pushover_emergency_priority_sends_retry_and_expire()
    {
        var (provider, handler) = Build(f => new PushoverNotificationProvider(f));

        await provider.SendAsync(
            PushoverConnection(",\"priority\":2"),
            new NotificationMessage(NotificationEventType.Test, "t", "b"));

        Assert.Equal("2", FieldOf(handler, "priority"));
        Assert.Equal("60", FieldOf(handler, "retry"));
        Assert.Equal("3600", FieldOf(handler, "expire"));
    }

    [Fact]
    public async Task Pushover_non_emergency_priority_sends_no_retry_or_expire()
    {
        var (provider, handler) = Build(f => new PushoverNotificationProvider(f));

        await provider.SendAsync(
            PushoverConnection(",\"priority\":1"),
            new NotificationMessage(NotificationEventType.Test, "t", "b"));

        Assert.Equal("1", FieldOf(handler, "priority"));
        Assert.False(HasPart(handler, "retry"));
        Assert.False(HasPart(handler, "expire"));
    }

    [Fact]
    public async Task Pushover_includes_url_and_url_title_only_for_absolute_urls()
    {
        var (provider, handler) = Build(f => new PushoverNotificationProvider(f));

        await provider.SendAsync(PushoverConnection(), new NotificationMessage(
            NotificationEventType.Test, "t", "b", SeriesTitle: "Naruto", Url: "/series/12"));
        Assert.False(HasPart(handler, "url"));

        await provider.SendAsync(PushoverConnection(), new NotificationMessage(
            NotificationEventType.Test, "t", "b", SeriesTitle: "Naruto", Url: "https://maki.example.com/series/12"));
        Assert.Equal("https://maki.example.com/series/12", FieldOf(handler, "url"));
        Assert.Equal("Naruto", FieldOf(handler, "url_title"));
    }

    [Fact]
    public async Task Pushover_truncates_url_title_and_drops_an_overlong_url()
    {
        var (provider, handler) = Build(f => new PushoverNotificationProvider(f));

        await provider.SendAsync(PushoverConnection(), new NotificationMessage(
            NotificationEventType.Test, "t", "b", SeriesTitle: new string('n', 300),
            Url: "https://maki.example.com/series/12"));
        Assert.Equal(100, FieldOf(handler, "url_title")!.Length);

        await provider.SendAsync(PushoverConnection(), new NotificationMessage(
            NotificationEventType.Test, "t", "b", SeriesTitle: "Naruto",
            Url: "https://maki.example.com/" + new string('a', 600)));
        Assert.False(HasPart(handler, "url"));
        Assert.False(HasPart(handler, "url_title"));
    }

    [Fact]
    public async Task Pushover_includes_device_when_configured()
    {
        var (provider, handler) = Build(f => new PushoverNotificationProvider(f));

        await provider.SendAsync(
            PushoverConnection(",\"device\":\"phone\""),
            new NotificationMessage(NotificationEventType.Test, "t", "b"));

        Assert.Equal("phone", FieldOf(handler, "device"));
    }

    [Fact]
    public async Task Pushover_truncates_message_and_title_past_limits()
    {
        var (provider, handler) = Build(f => new PushoverNotificationProvider(f));

        await provider.SendAsync(PushoverConnection(), new NotificationMessage(
            NotificationEventType.Test, new string('t', 500), new string('b', 2000)));

        Assert.Equal(250, FieldOf(handler, "title")!.Length);
        Assert.Equal(1024, FieldOf(handler, "message")!.Length);
    }

    [Fact]
    public async Task Pushover_attaches_poster_when_cover_store_resolves_one()
    {
        var poster = Path.Combine(Path.GetTempPath(), $"maki-poster-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(poster, [0xFF, 0xD8, 0xFF, 0xD9]);
        try
        {
            var (provider, handler) = Build(f => new PushoverNotificationProvider(f, new StubCoverStore(poster)));

            await provider.SendAsync(PushoverConnection(), new NotificationMessage(
                NotificationEventType.ChapterDownloaded, "t", "b", SeriesId: 7));

            Assert.True(HasPart(handler, "attachment"));
            Assert.Contains("image/jpeg", handler.Body);
        }
        finally
        {
            File.Delete(poster);
        }
    }

    [Fact]
    public async Task Pushover_sends_no_attachment_when_series_has_no_poster()
    {
        var (provider, handler) = Build(f => new PushoverNotificationProvider(f, new StubCoverStore(null)));

        await provider.SendAsync(PushoverConnection(), new NotificationMessage(
            NotificationEventType.ChapterDownloaded, "t", "b", SeriesId: 7));

        Assert.False(HasPart(handler, "attachment"));
    }

    [Fact]
    public async Task Pushover_throws_when_required_fields_missing()
    {
        var (provider, _) = Build(f => new PushoverNotificationProvider(f));
        var connection = new Notification { Type = NotificationType.Pushover, ConfigJson = """{"appToken":"a"}""" };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.SendAsync(connection, new NotificationMessage(NotificationEventType.Test, "t", "b")));
    }

    [Fact]
    public async Task Pushover_non_success_status_throws_without_leaking_tokens()
    {
        var (provider, _) = Build(f => new PushoverNotificationProvider(f), HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<NotificationDeliveryException>(() =>
            provider.SendAsync(PushoverConnection(), new NotificationMessage(NotificationEventType.Test, "t", "b")));

        Assert.DoesNotContain("app123", ex.Message);
        Assert.DoesNotContain("user456", ex.Message);
        Assert.Contains("Pushover", ex.Message);
        Assert.Contains("400", ex.Message);
    }
}
