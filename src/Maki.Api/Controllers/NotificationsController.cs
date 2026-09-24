using Microsoft.AspNetCore.Authorization;
using Maki.Api.Auth;
using System.Globalization;
using System.Text.Json;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Notifications;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/notifications")]
// Admin-only throughout: a Connect target is an outbound webhook belonging to the instance, and its
// ConfigJson holds webhook URLs and tokens in plaintext.
[Authorize(Policy = Policies.Admin)]
public class NotificationsController(
    ILocalizer localizer,
    MakiDbContext db,
    NotificationService notifications) : ControllerBase
{
    public record EventsDto(
        bool ChapterDownloaded, bool DownloadFailed, bool NewChapterAvailable,
        bool ImportCompleted, bool HealthIssue, bool UpdateAvailable);
    public record NotificationDto(
        int Id, string Name, NotificationType Type, bool Enabled,
        Dictionary<string, string> Config, EventsDto Events);
    public record NotificationRequest(
        string Name, NotificationType Type, bool Enabled,
        Dictionary<string, string>? Config, EventsDto Events);

    [HttpGet("providers")]
    public IActionResult Providers() => Ok(notifications.Descriptors);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok((await db.Notifications.OrderBy(n => n.Id).ToListAsync(ct)).Select(ToDto));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] NotificationRequest request, CancellationToken ct)
    {
        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var entity = new Notification();
        Apply(entity, request);
        db.Notifications.Add(entity);
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(entity));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] NotificationRequest request, CancellationToken ct)
    {
        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var entity = await db.Notifications.FirstOrDefaultAsync(n => n.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        Apply(entity, request);
        await db.SaveChangesAsync(ct);
        return Ok(ToDto(entity));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var entity = await db.Notifications.FirstOrDefaultAsync(n => n.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        db.Notifications.Remove(entity);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Sends a test message through the supplied (possibly unsaved) connection config.</summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] NotificationRequest request, CancellationToken ct)
    {
        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var transient = new Notification();
        Apply(transient, request);
        // The one outbound message with a person behind it: an admin just pressed Test and is
        // watching for it, so it answers in their language rather than the instance's.
        var message = new NotificationMessage(
            NotificationEventType.Test,
            Title: localizer.Get("notify.test.title"),
            Body: localizer.Get("notify.test.body", new { name = request.Name }));

        try
        {
            await notifications.SendToAsync(transient, message, ct);
            return Ok(new { success = true });
        }
        catch (NotificationDeliveryException ex) when (ex.StatusCode is { } status)
        {
            const string key = "error.notifications.deliveryRejected";
            var error = localizer.Get(key, new
            {
                provider = ex.Provider,
                status = status.ToString(CultureInfo.InvariantCulture),
                hasDetail = string.IsNullOrWhiteSpace(ex.Detail) ? "no" : "yes",
                detail = ex.Detail ?? string.Empty
            });
            return StatusCode(StatusCodes.Status502BadGateway, new { success = false, code = key, error });
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { success = false, error = ex.Message });
        }
        catch (Exception)
        {
            const string key = "error.notifications.deliveryFailed";
            return StatusCode(StatusCodes.Status502BadGateway,
                new { success = false, code = key, error = localizer.Get(key) });
        }
    }

    /// <summary>Checks the request against its provider's descriptor. Returns null when valid.</summary>
    private IActionResult? Validate(NotificationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return this.Fail(localizer, "error.notifications.nameRequired");
        }

        if (notifications.DescriptorFor(request.Type) is not { } descriptor)
        {
            return this.Fail(localizer, "error.notifications.unknownType");
        }

        var config = Incoming(request.Config);
        foreach (var field in descriptor.Fields)
        {
            config.TryGetValue(field.Key, out var value);
            if (string.IsNullOrWhiteSpace(value))
            {
                if (field.Required)
                {
                    return RequiredFailure(request.Type, field.Key);
                }

                continue;
            }

            value = value.Trim();
            var isNumber = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number);
            var failure = field.Kind switch
            {
                NotificationFieldKind.Url when !IsHttpUrl(value) =>
                    this.Fail(localizer, "error.notifications.urlMustBeHttp"),
                NotificationFieldKind.Number when !isNumber =>
                    this.Fail(localizer, "error.notifications.fieldMustBeNumber", new { field = field.Key }),
                NotificationFieldKind.Number when (field.Min is { } min && number < min) || (field.Max is { } max && number > max) =>
                    RangeFailure(field),
                NotificationFieldKind.Boolean when value is not ("true" or "false") =>
                    this.Fail(localizer, "error.notifications.fieldMustBeBoolean", new { field = field.Key }),
                _ => null
            };
            if (failure is not null)
            {
                return failure;
            }
        }

        if (notifications.ValidateConfig(request.Type, new NotificationFields(config)) is { } key)
        {
            return this.Fail(localizer, key);
        }

        return null;
    }

    private IActionResult RangeFailure(NotificationField field) =>
        this.Fail(localizer, "error.notifications.fieldOutOfRange", new
        {
            field = field.Key,
            min = (field.Min ?? int.MinValue).ToString(CultureInfo.InvariantCulture),
            max = (field.Max ?? int.MaxValue).ToString(CultureInfo.InvariantCulture)
        });

    private IActionResult RequiredFailure(NotificationType type, string key) => (type, key) switch
    {
        (NotificationType.Discord, "webhookUrl") => this.Fail(localizer, "error.notifications.discordWebhookUrlRequired"),
        (NotificationType.Webhook, "url") => this.Fail(localizer, "error.notifications.webhookUrlRequired"),
        _ => this.Fail(localizer, "error.notifications.fieldRequired", new { field = key })
    };

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static Dictionary<string, string> Incoming(Dictionary<string, string>? config)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (config is null)
        {
            return result;
        }

        foreach (var (key, value) in config)
        {
            result[key] = value;
        }

        return result;
    }

    private void Apply(Notification entity, NotificationRequest request)
    {
        entity.Name = request.Name.Trim();
        entity.Type = request.Type;
        entity.Enabled = request.Enabled;
        entity.ConfigJson = SerializeConfig(request.Type, request.Config);
        entity.OnChapterDownloaded = request.Events.ChapterDownloaded;
        entity.OnDownloadFailed = request.Events.DownloadFailed;
        entity.OnNewChapterAvailable = request.Events.NewChapterAvailable;
        entity.OnImportCompleted = request.Events.ImportCompleted;
        entity.OnHealthIssue = request.Events.HealthIssue;
        entity.OnUpdateAvailable = request.Events.UpdateAvailable;
    }

    /// <summary>Keeps only the descriptor's keys, under the descriptor's spelling, so nothing else gets stored.</summary>
    private string SerializeConfig(NotificationType type, Dictionary<string, string>? config)
    {
        var incoming = Incoming(config);
        var stored = new Dictionary<string, string>();
        foreach (var field in notifications.DescriptorFor(type)?.Fields ?? [])
        {
            if (incoming.TryGetValue(field.Key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                stored[field.Key] = value.Trim();
            }
        }

        return JsonSerializer.Serialize(stored);
    }

    private NotificationDto ToDto(Notification n)
    {
        var fields = NotificationConfig.Fields(n.ConfigJson);
        var config = new Dictionary<string, string>();
        foreach (var field in notifications.DescriptorFor(n.Type)?.Fields ?? [])
        {
            if (fields[field.Key] is { } value)
            {
                config[field.Key] = value;
            }
        }

        return new NotificationDto(n.Id, n.Name, n.Type, n.Enabled, config, new EventsDto(
            n.OnChapterDownloaded, n.OnDownloadFailed, n.OnNewChapterAvailable,
            n.OnImportCompleted, n.OnHealthIssue, n.OnUpdateAvailable));
    }
}
