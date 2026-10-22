using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using Maki.Api.Controllers;
using Maki.Api.Hubs;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Web Push: who owns a subscription, the per-user filter, what the sender does with a push
/// service's answer, and that the inbox hands the sender text in the recipient's language.
/// </summary>
public sealed class WebPushTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Endpoint = "https://push.example.com/send/abc";

    private readonly TestDb _db = new();
    private readonly int _alice;
    private readonly int _bob;

    public WebPushTests()
    {
        _alice = _db.SeedUser("alice", MakiPermission.Admin);
        _bob = _db.SeedUser("bob", MakiPermission.None);
    }

    public void Dispose() => _db.Dispose();

    private static readonly string P256dh = WebPushKeys.Generate().PublicKey;
    private static readonly string AuthSecret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    private static PushSubscriptionRequest Subscription(string endpoint = Endpoint) =>
        new(endpoint, new PushSubscriptionKeys(P256dh, AuthSecret));

    private PushController Controller(int userId, StubPushService? service = null)
    {
        var db = _db.NewContext(userId);
        var user = new TestCurrentUser(userId, permissions: userId == _alice ? MakiPermission.Admin : MakiPermission.None);
        var keys = new WebPushKeys(new FakeAppSettings());
        return new PushController(db, new TestLocalizer(), user, new UserSettingsService(db, user), keys,
            Sender(service ?? new StubPushService(HttpStatusCode.Created), keys), new StoppedClock(T0))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private WebPushSender Sender(StubPushService service, WebPushKeys? keys = null) =>
        new(_db.ScopeFactory(), service, keys ?? new WebPushKeys(new FakeAppSettings()), new StoppedClock(T0),
            NullLogger<WebPushSender>.Instance);

    private List<PushSubscription> Rows()
    {
        using var db = _db.NewContext();
        return db.PushSubscriptions.OrderBy(s => s.Id).ToList();
    }

    private void SeedSubscription(int userId, string endpoint = Endpoint)
    {
        using var db = _db.NewContext();
        db.PushSubscriptions.Add(new PushSubscription
        {
            UserId = userId, Endpoint = endpoint, P256dh = P256dh, Auth = AuthSecret, CreatedAt = T0.UtcDateTime
        });
        db.SaveChanges();
    }

    // ---- subscriptions ----

    [Fact]
    public async Task Subscribing_twice_keeps_one_row()
    {
        Assert.IsType<OkObjectResult>(await Controller(_alice).Subscribe(Subscription(), default));
        Assert.IsType<OkObjectResult>(await Controller(_alice).Subscribe(Subscription(), default));

        var row = Assert.Single(Rows());
        Assert.Equal(_alice, row.UserId);
        Assert.Equal(T0.UtcDateTime, row.CreatedAt);
    }

    [Fact]
    public async Task A_browser_that_signs_in_as_someone_else_moves_to_them()
    {
        await Controller(_alice).Subscribe(Subscription(), default);
        await Controller(_bob).Subscribe(Subscription(), default);

        Assert.Equal(_bob, Assert.Single(Rows()).UserId);
    }

    [Theory]
    [InlineData("http://push.example.com/send/abc")]
    [InlineData("https://127.0.0.1/send/abc")]
    [InlineData("https://localhost/send/abc")]
    [InlineData("not a url")]
    public async Task An_endpoint_off_the_public_https_internet_is_refused(string endpoint)
    {
        Assert.IsType<BadRequestObjectResult>(await Controller(_alice).Subscribe(Subscription(endpoint), default));
        Assert.Empty(Rows());
    }

    [Fact]
    public async Task Keys_of_the_wrong_size_are_refused()
    {
        var request = new PushSubscriptionRequest(Endpoint, new PushSubscriptionKeys(AuthSecret, AuthSecret));

        Assert.IsType<BadRequestObjectResult>(await Controller(_alice).Subscribe(request, default));
    }

    [Fact]
    public async Task A_user_keeps_at_most_the_newest_devices_up_to_the_cap()
    {
        SeedSubscription(_bob, "https://push.example.com/send/bob");
        for (var i = 0; i <= PushController.MaxSubscriptionsPerUser; i++)
        {
            Assert.IsType<OkObjectResult>(await Controller(_alice).Subscribe(Subscription($"{Endpoint}/{i}"), default));
        }

        var alice = Rows().Where(r => r.UserId == _alice).Select(r => r.Endpoint).ToList();
        Assert.Equal(PushController.MaxSubscriptionsPerUser, alice.Count);
        Assert.DoesNotContain($"{Endpoint}/0", alice);
        Assert.Contains($"{Endpoint}/{PushController.MaxSubscriptionsPerUser}", alice);
        Assert.Single(Rows(), r => r.UserId == _bob);
    }

    [Fact]
    public async Task A_user_cannot_delete_anothers_endpoint()
    {
        SeedSubscription(_bob);

        Assert.IsType<NoContentResult>(await Controller(_alice).Unsubscribe(new PushEndpointRequest(Endpoint), default));
        Assert.Single(Rows());

        await Controller(_bob).Unsubscribe(new PushEndpointRequest(Endpoint), default);
        Assert.Empty(Rows());
    }

    [Fact]
    public async Task A_test_with_no_device_is_not_found()
    {
        SeedSubscription(_bob);

        Assert.IsType<NotFoundObjectResult>(await Controller(_alice).Test(default));
    }

    [Fact]
    public async Task A_test_reaches_the_callers_devices()
    {
        SeedSubscription(_alice);
        var service = new StubPushService(HttpStatusCode.Created);

        var result = Assert.IsType<OkObjectResult>(await Controller(_alice, service).Test(default));

        Assert.Equal("{ sent = 1, failed = 0, error =  }", result.Value!.ToString());
        Assert.Equal(Endpoint, Assert.Single(service.Requests).RequestUri!.ToString());
    }

    // ---- filter ----

    [Fact]
    public async Task The_filter_defaults_to_new_and_downloaded_chapters()
    {
        var result = Assert.IsType<OkObjectResult>(await Controller(_bob).GetPrefs(default));

        Assert.Equal(["newChapterAvailable", "chapterDownloaded"], Types(result));
        Assert.Equal(PushPrefs.Defaults.ToHashSet(), PushPrefs.Parse(null));
        Assert.Equal(PushPrefs.Defaults.ToHashSet(), PushPrefs.Parse("not json"));
    }

    [Fact]
    public async Task Saving_the_filter_drops_unknown_and_admin_only_types_for_a_reader()
    {
        var saved = await Controller(_bob).SavePrefs(
            new PushPrefsRequest(["levelUp", "healthIssue", "noSuchThing"]), default);

        Assert.Equal(["levelUp"], Types(saved));
        Assert.Equal(["levelUp"], Types(await Controller(_bob).GetPrefs(default)));
    }

    [Fact]
    public async Task An_empty_filter_is_kept_and_the_default_one_stores_nothing()
    {
        await Controller(_bob).SavePrefs(new PushPrefsRequest([]), default);
        Assert.Empty(Types(await Controller(_bob).GetPrefs(default)));

        await Controller(_bob).SavePrefs(new PushPrefsRequest(["chapterDownloaded", "newChapterAvailable"]), default);
        using var db = _db.NewContext();
        Assert.False(db.UserSettings.Any(s => s.UserId == _bob && s.Key == SettingKeys.NotificationsPush));
    }

    private static string[] Types(IActionResult result)
    {
        var value = Assert.IsType<OkObjectResult>(result).Value!;
        return (string[])value.GetType().GetProperty("types")!.GetValue(value)!;
    }

    // ---- sender ----

    [Fact]
    public async Task A_delivered_push_is_vapid_signed_and_carries_ttl_and_topic()
    {
        SeedSubscription(_alice);
        var service = new StubPushService(HttpStatusCode.Created);

        var (sent, failed, _) = await Sender(service).SendAsync(
            new WebPushNotification(_alice, "Title", "Body", "/series/4", "series-4"), default);

        Assert.Equal((1, 0), (sent, failed));
        var request = Assert.Single(service.Requests);
        Assert.Equal("vapid", request.Headers.Authorization!.Scheme);
        Assert.Equal("86400", request.Headers.GetValues("TTL").Single());
        var topic = request.Headers.GetValues("Topic").Single();
        Assert.Matches("^[0-9a-f]{32}$", topic);
        Assert.Equal(WebPushSender.TopicFor("series-4"), topic);
        Assert.NotEqual(WebPushSender.TopicFor("series-5"), topic);
        Assert.Equal("aes128gcm", request.Content!.Headers.ContentEncoding.Single());
        Assert.Equal(T0.UtcDateTime, Assert.Single(Rows()).LastSuccessAt);
    }

    [Theory]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_subscription_the_push_service_no_longer_knows_is_deleted(HttpStatusCode status)
    {
        SeedSubscription(_alice);

        var (sent, failed, _) = await Sender(new StubPushService(status)).SendAsync(
            new WebPushNotification(_alice, "Title", "Body", "/", null), default);

        Assert.Equal((0, 1), (sent, failed));
        Assert.Empty(Rows());
    }

    [Fact]
    public async Task Other_failures_count_and_the_row_goes_at_the_limit()
    {
        SeedSubscription(_alice);
        var sender = Sender(new StubPushService(HttpStatusCode.InternalServerError));
        var push = new WebPushNotification(_alice, "Title", "Body", "/", null);

        for (var i = 1; i < WebPushSender.MaxFailures; i++)
        {
            await sender.SendAsync(push, default);
            Assert.Equal(i, Assert.Single(Rows()).FailureCount);
        }

        await sender.SendAsync(push, default);
        Assert.Empty(Rows());
    }

    [Fact]
    public async Task A_refusal_reports_the_push_services_reason()
    {
        SeedSubscription(_alice);
        var service = new StubPushService(HttpStatusCode.Forbidden, """{"reason":"BadJwtToken"}""");

        var result = await Sender(service).SendAsync(new WebPushNotification(_alice, "Title", "Body", "/", null), default);

        Assert.Equal((0, 1, "403 BadJwtToken"), result);
    }

    [Fact]
    public async Task Only_the_recipients_own_devices_are_sent_to()
    {
        SeedSubscription(_alice, "https://push.example.com/send/alice");
        SeedSubscription(_bob, "https://push.example.com/send/bob");
        var service = new StubPushService(HttpStatusCode.Created);

        await Sender(service).SendAsync(new WebPushNotification(_bob, "Title", "Body", "/", null), default);

        Assert.Equal("https://push.example.com/send/bob", Assert.Single(service.Requests).RequestUri!.ToString());
    }

    [Fact]
    public async Task The_vapid_key_pair_is_generated_once_and_stored()
    {
        var settings = new FakeAppSettings();

        var first = await new WebPushKeys(settings).GetAsync(default);
        var second = await new WebPushKeys(settings).GetAsync(default);

        Assert.Equal(first, second);
        Assert.Equal(first.PublicKey, await settings.GetAsync(SettingKeys.PushVapidPublicKey));
        Assert.Equal(65, Base64Url.DecodeFromChars(first.PublicKey).Length);
        Assert.Equal(32, Base64Url.DecodeFromChars(first.PrivateKey).Length);
    }

    // ---- inbox ----

    [Fact]
    public async Task The_inbox_pushes_in_each_recipients_language_and_only_the_types_they_chose()
    {
        var seriesId = _db.SeedSeries("Berserk");
        _db.SetUserConfig(_bob, (SettingKeys.NotificationsPush, "[\"levelUp\"]"));
        var scopeFactory = _db.ScopeFactory(new LocaleByUser(new Dictionary<int, string> { [_alice] = "de" }));
        var push = new RecordingPushSender(scopeFactory);
        var inbox = new InboxService(scopeFactory, new InboxAudienceResolver(scopeFactory),
            new EventBroadcaster(new NoopHubContext(), scopeFactory), push, new StoppedClock(T0),
            NullLogger<InboxService>.Instance);

        await inbox.RaiseAsync(InboxEventType.ChapterDownloaded,
            new InboxMessage("inbox.chapter.downloaded", InboxMessage.Args(new { chapter = "3" }),
                SeriesId: seriesId, Url: $"/series/{seriesId}"),
            InboxAudience.Admins);
        await inbox.RaiseAsync(InboxEventType.LevelUp,
            new InboxMessage("inbox.levelUp", InboxMessage.Args(new { level = 2, previous = 1 })),
            InboxAudience.User(_bob));

        Assert.Equal(2, push.Sent.Count);
        var chapter = Assert.Single(push.Sent, p => p.UserId == _alice);
        Assert.StartsWith("de:inbox.chapter.downloaded.title", chapter.Title, StringComparison.Ordinal);
        Assert.StartsWith("de:inbox.chapter.downloaded.body", chapter.Body, StringComparison.Ordinal);
        Assert.Equal($"/series/{seriesId}", chapter.Url);
        Assert.Equal($"series-{seriesId}", chapter.Tag);
        var level = Assert.Single(push.Sent, p => p.UserId == _bob);
        Assert.StartsWith("en:inbox.levelUp.title", level.Title, StringComparison.Ordinal);
        Assert.Equal("/notifications", level.Url);
        Assert.Null(level.Tag);
    }

    private sealed class LocaleByUser(IReadOnlyDictionary<int, string> locales) : IUserLocaleResolver
    {
        public Task<string> ResolveAsync(int userId, CancellationToken ct = default) =>
            Task.FromResult(locales.GetValueOrDefault(userId, "en"));

        public Task<string> DefaultAsync(CancellationToken ct = default) => Task.FromResult("en");

        public void Forget(int userId) { }
    }
}

/// <summary>A <see cref="WebPushSender"/> that records what it is handed instead of queueing it.</summary>
internal sealed class RecordingPushSender(IServiceScopeFactory scopeFactory)
    : WebPushSender(scopeFactory, new StubPushService(HttpStatusCode.Created),
        new WebPushKeys(new FakeAppSettings()), TimeProvider.System, NullLogger<WebPushSender>.Instance)
{
    public List<WebPushNotification> Sent { get; } = [];

    public override void Enqueue(WebPushNotification notification)
    {
        lock (Sent)
        {
            Sent.Add(notification);
        }
    }
}

/// <summary>A push service that answers every request with one status, and an <see cref="IHttpClientFactory"/> over it.</summary>
internal sealed class StubPushService(HttpStatusCode status, string? body = null) : HttpMessageHandler, IHttpClientFactory
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        var response = new HttpResponseMessage(status);
        if (body is not null)
        {
            response.Content = new StringContent(body);
        }

        return Task.FromResult(response);
    }
}
