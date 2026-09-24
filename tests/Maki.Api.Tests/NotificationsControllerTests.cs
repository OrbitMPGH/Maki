using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Notifications;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

public class NotificationsControllerTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private sealed class StubProvider(
        NotificationType type, IReadOnlyList<NotificationField> fields, bool throws = false,
        Exception? error = null, string? validationKey = null) : INotificationProvider
    {
        public NotificationType Type => type;
        public NotificationProviderDescriptor Descriptor => new(type, fields);

        public Task SendAsync(Notification connection, NotificationMessage message, CancellationToken ct = default) =>
            error is not null ? throw error
            : throws ? throw new InvalidOperationException("unreachable")
            : Task.CompletedTask;

        public string? Validate(NotificationFields fields) => validationKey;
    }

    private static StubProvider Discord(Exception error) => new(NotificationType.Discord,
        [new NotificationField("webhookUrl", NotificationFieldKind.Url, Required: true)], error: error);

    private static StubProvider Discord(bool throws = false) => new(NotificationType.Discord,
        [new NotificationField("webhookUrl", NotificationFieldKind.Url, Required: true)], throws);

    private static StubProvider Webhook() => new(NotificationType.Webhook,
    [
        new NotificationField("url", NotificationFieldKind.Url, Required: true),
        new NotificationField("bearerToken", NotificationFieldKind.Secret)
    ]);

    private static StubProvider Ntfy() => new(NotificationType.Ntfy,
    [
        new NotificationField("serverUrl", NotificationFieldKind.Url, Required: true),
        new NotificationField("topic", NotificationFieldKind.Text, Required: true),
        new NotificationField("priority", NotificationFieldKind.Number, Min: 1, Max: 5),
        new NotificationField("silent", NotificationFieldKind.Boolean)
    ]);

    private NotificationsController Controller(params INotificationProvider[] providers)
    {
        if (providers.Length == 0)
        {
            providers = [Discord(), Webhook(), Ntfy()];
        }

        var service = new NotificationService(_db.ScopeFactory(), providers, NullLogger<NotificationService>.Instance);
        return new NotificationsController(new TestLocalizer(), _db.NewContext(), service);
    }

    private static NotificationsController.EventsDto Events() => new(true, false, false, false, false, false);

    private static NotificationsController.NotificationRequest DiscordRequest(
        string name = "test", string? webhookUrl = "https://discord.com/api/webhooks/abc")
    {
        var config = new Dictionary<string, string>();
        if (webhookUrl is not null)
        {
            config["webhookUrl"] = webhookUrl;
        }

        return new(name, NotificationType.Discord, true, config, Events());
    }

    private static NotificationsController.NotificationRequest NtfyRequest(Dictionary<string, string> config) =>
        new("ntfy", NotificationType.Ntfy, true, config, Events());

    private static string? Code(IActionResult result)
    {
        var body = Assert.IsType<BadRequestObjectResult>(result).Value;
        return (string?)body!.GetType().GetProperty("code")!.GetValue(body);
    }

    [Fact]
    public async Task Create_persists_and_returns_the_connection()
    {
        var result = await Controller().Create(DiscordRequest(), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<NotificationsController.NotificationDto>(ok.Value);
        Assert.Equal("test", dto.Name);
        Assert.Equal("https://discord.com/api/webhooks/abc", dto.Config["webhookUrl"]);

        using var db = _db.NewContext();
        var row = Assert.Single(db.Notifications);
        Assert.Equal("https://discord.com/api/webhooks/abc", NotificationConfig.Discord(row.ConfigJson).WebhookUrl);
    }

    [Fact]
    public async Task Create_rejects_a_missing_url()
    {
        var result = await Controller().Create(DiscordRequest(webhookUrl: null), CancellationToken.None);
        Assert.Equal("error.notifications.discordWebhookUrlRequired", Code(result));
    }

    [Fact]
    public async Task Create_rejects_a_non_http_url()
    {
        var result = await Controller().Create(DiscordRequest(webhookUrl: "ftp://nope"), CancellationToken.None);
        Assert.Equal("error.notifications.urlMustBeHttp", Code(result));
    }

    [Fact]
    public async Task Create_rejects_a_missing_generic_required_field()
    {
        var result = await Controller().Create(
            NtfyRequest(new() { ["serverUrl"] = "https://ntfy.sh" }), CancellationToken.None);
        Assert.Equal("error.notifications.fieldRequired", Code(result));
    }

    [Fact]
    public async Task Create_rejects_a_non_numeric_number_field()
    {
        var result = await Controller().Create(NtfyRequest(new()
        {
            ["serverUrl"] = "https://ntfy.sh", ["topic"] = "maki", ["priority"] = "high"
        }), CancellationToken.None);
        Assert.Equal("error.notifications.fieldMustBeNumber", Code(result));
    }

    [Fact]
    public async Task Create_rejects_a_malformed_boolean_field()
    {
        var result = await Controller().Create(NtfyRequest(new()
        {
            ["serverUrl"] = "https://ntfy.sh", ["topic"] = "maki", ["silent"] = "yes"
        }), CancellationToken.None);
        Assert.Equal("error.notifications.fieldMustBeBoolean", Code(result));
    }

    [Fact]
    public async Task Create_rejects_a_type_with_no_registered_provider()
    {
        var request = new NotificationsController.NotificationRequest(
            "tg", NotificationType.Telegram, true, new() { ["botToken"] = "x" }, Events());

        var result = await Controller().Create(request, CancellationToken.None);

        Assert.Equal("error.notifications.unknownType", Code(result));
    }

    [Fact]
    public async Task Create_strips_keys_the_descriptor_does_not_declare()
    {
        var result = await Controller().Create(NtfyRequest(new()
        {
            ["serverUrl"] = "https://ntfy.sh", ["topic"] = " maki ", ["priority"] = "4", ["junk"] = "payload"
        }), CancellationToken.None);

        var dto = Assert.IsType<NotificationsController.NotificationDto>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.False(dto.Config.ContainsKey("junk"));
        Assert.Equal("maki", dto.Config["topic"]);

        using var db = _db.NewContext();
        var json = Assert.Single(db.Notifications).ConfigJson;
        Assert.DoesNotContain("junk", json);
        Assert.Equal("4", NotificationConfig.Fields(json)["priority"]);
    }

    [Fact]
    public async Task Existing_rows_read_back_through_the_descriptor_keys()
    {
        using (var db = _db.NewContext())
        {
            db.Notifications.Add(new Notification
            {
                Name = "legacy",
                Type = NotificationType.Webhook,
                ConfigJson = """{"url":"https://example.com/hook","bearerToken":"secret"}"""
            });
            await db.SaveChangesAsync();
        }

        var ok = Assert.IsType<OkObjectResult>(await Controller().List(CancellationToken.None));
        var dto = Assert.Single(Assert.IsAssignableFrom<IEnumerable<NotificationsController.NotificationDto>>(ok.Value));
        Assert.Equal("https://example.com/hook", dto.Config["url"]);
        Assert.Equal("secret", dto.Config["bearerToken"]);
    }

    [Fact]
    public void Providers_lists_registered_descriptors_in_enum_order()
    {
        var ok = Assert.IsType<OkObjectResult>(Controller(Ntfy(), Webhook(), Discord()).Providers());

        var descriptors = Assert.IsAssignableFrom<IEnumerable<NotificationProviderDescriptor>>(ok.Value).ToList();
        Assert.Equal(
            [NotificationType.Discord, NotificationType.Webhook, NotificationType.Ntfy],
            descriptors.Select(d => d.Type));
        Assert.Equal(["url", "bearerToken"], descriptors[1].Fields.Select(f => f.Key));
    }

    [Fact]
    public async Task Update_missing_returns_not_found()
    {
        var result = await Controller().Update(999, DiscordRequest(), CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Update_changes_fields()
    {
        var created = (NotificationsController.NotificationDto)
            ((OkObjectResult)await Controller().Create(DiscordRequest(), CancellationToken.None)).Value!;

        var result = await Controller().Update(created.Id, DiscordRequest(name: "renamed"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("renamed", ((NotificationsController.NotificationDto)ok.Value!).Name);
    }

    [Fact]
    public async Task Delete_removes_the_connection()
    {
        var created = (NotificationsController.NotificationDto)
            ((OkObjectResult)await Controller().Create(DiscordRequest(), CancellationToken.None)).Value!;

        var result = await Controller().Delete(created.Id, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        using var db = _db.NewContext();
        Assert.Empty(db.Notifications);
    }

    [Fact]
    public async Task Delete_missing_returns_not_found()
    {
        Assert.IsType<NotFoundResult>(await Controller().Delete(999, CancellationToken.None));
    }

    [Fact]
    public async Task Test_returns_ok_when_the_provider_succeeds()
    {
        var result = await Controller(Discord()).Test(DiscordRequest(), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Test_returns_bad_gateway_when_the_provider_throws()
    {
        var result = await Controller(Discord(throws: true)).Test(DiscordRequest(), CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status502BadGateway, status.StatusCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("6")]
    [InlineData("-3")]
    public async Task Create_rejects_a_number_outside_the_descriptor_range(string priority)
    {
        var result = await Controller().Create(NtfyRequest(new()
        {
            ["serverUrl"] = "https://ntfy.sh", ["topic"] = "maki", ["priority"] = priority
        }), CancellationToken.None);

        Assert.Equal("error.notifications.fieldOutOfRange", Code(result));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("5")]
    public async Task Create_accepts_a_number_on_the_range_bounds(string priority)
    {
        var result = await Controller().Create(NtfyRequest(new()
        {
            ["serverUrl"] = "https://ntfy.sh", ["topic"] = "maki", ["priority"] = priority
        }), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Create_fails_with_the_provider_validation_key()
    {
        var apprise = new StubProvider(NotificationType.Apprise,
            [new NotificationField("serverUrl", NotificationFieldKind.Url, Required: true)],
            validationKey: "error.notifications.appriseTarget");

        var result = await Controller(apprise).Create(
            new("apprise", NotificationType.Apprise, true,
                new() { ["serverUrl"] = "https://apprise.example.com" }, Events()),
            CancellationToken.None);

        Assert.Equal("error.notifications.appriseTarget", Code(result));
    }

    [Fact]
    public void Providers_serialise_min_and_max_on_each_field()
    {
        var ok = Assert.IsType<OkObjectResult>(Controller().Providers());

        var json = System.Text.Json.JsonSerializer.Serialize(ok.Value,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var ntfy = doc.RootElement.EnumerateArray().Last();
        var priority = ntfy.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "priority");
        Assert.Equal(1, priority.GetProperty("min").GetInt32());
        Assert.Equal(5, priority.GetProperty("max").GetInt32());
        var topic = ntfy.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "topic");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, topic.GetProperty("min").ValueKind);
    }

    private static (int? status, string? code, string? error) Gateway(IActionResult result)
    {
        var obj = Assert.IsType<ObjectResult>(result);
        var body = obj.Value!;
        return (obj.StatusCode,
            (string?)body.GetType().GetProperty("code")?.GetValue(body),
            (string?)body.GetType().GetProperty("error")?.GetValue(body));
    }

    [Fact]
    public async Task Test_maps_a_delivery_exception_to_a_localized_bad_gateway()
    {
        var result = await Controller(Discord(new NotificationDeliveryException("Telegram", 400, "chat not found")))
            .Test(DiscordRequest(), CancellationToken.None);

        var (status, code, error) = Gateway(result);
        Assert.Equal(StatusCodes.Status502BadGateway, status);
        Assert.Equal("error.notifications.deliveryRejected", code);
        Assert.Equal(
            "error.notifications.deliveryRejected(provider=Telegram, status=400, hasDetail=yes, detail=chat not found)",
            error);
    }

    [Fact]
    public async Task Test_delivery_exception_without_detail_says_so()
    {
        var result = await Controller(Discord(new NotificationDeliveryException("ntfy", 401, null)))
            .Test(DiscordRequest(), CancellationToken.None);

        var (_, _, error) = Gateway(result);
        Assert.Contains("hasDetail=no", error);
    }

    [Fact]
    public async Task Test_other_exceptions_keep_the_generic_delivery_failure()
    {
        var result = await Controller(Discord(new HttpRequestException("https://secret.example.com/token")))
            .Test(DiscordRequest(), CancellationToken.None);

        var (status, code, error) = Gateway(result);
        Assert.Equal(StatusCodes.Status502BadGateway, status);
        Assert.Equal("error.notifications.deliveryFailed", code);
        Assert.DoesNotContain("secret", error);
    }

    private int SeedTag(string label)
    {
        using var db = _db.NewContext();
        var tag = new Tag { Label = label };
        db.Tags.Add(tag);
        db.SaveChanges();
        return tag.Id;
    }

    [Fact]
    public async Task Tag_ids_and_new_events_round_trip()
    {
        var a = SeedTag("a");
        var b = SeedTag("b");
        var c = SeedTag("c");
        var events = new NotificationsController.EventsDto(
            false, false, false, false, false, false,
            SeriesAdded: true, SeriesRemoved: false, RequestSubmitted: true, RequestResolved: false, ManualMatchNeeded: true);

        var created = (NotificationsController.NotificationDto)((OkObjectResult)await Controller().Create(
            DiscordRequest() with { Events = events, TagIds = [b, a, a] }, CancellationToken.None)).Value!;
        Assert.Equal([a, b], created.TagIds);
        Assert.Equal(events, created.Events);

        await Controller().Update(created.Id, DiscordRequest() with { Events = events, TagIds = [c, b] }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(await Controller().List(CancellationToken.None));
        var listed = Assert.Single(Assert.IsAssignableFrom<IEnumerable<NotificationsController.NotificationDto>>(ok.Value));
        Assert.Equal([b, c], listed.TagIds);
        Assert.Equal(events, listed.Events);
    }

    [Fact]
    public async Task Omitted_tag_ids_mean_every_series()
    {
        var created = (NotificationsController.NotificationDto)
            ((OkObjectResult)await Controller().Create(DiscordRequest(), CancellationToken.None)).Value!;

        Assert.Empty(created.TagIds);
    }

    [Fact]
    public async Task Unknown_tag_ids_are_rejected()
    {
        var known = SeedTag("known");

        var result = await Controller().Create(DiscordRequest() with { TagIds = [known, known + 100] }, CancellationToken.None);

        Assert.Equal("error.notifications.unknownTag", Code(result));
        using var db = _db.NewContext();
        Assert.Empty(db.Notifications);
    }
}
