using System.Text.Json;
using Mangarr.Api.Services;
using Mangarr.Core.Entities;
using Mangarr.Core.Notifications;
using Mangarr.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Controllers;

[ApiController]
[Route("api/v1/notifications")]
public class NotificationsController(
    MangarrDbContext db,
    NotificationService notifications) : ControllerBase
{
    public record ConfigDto(string? WebhookUrl, string? Url, string? BearerToken);
    public record EventsDto(
        bool ChapterDownloaded, bool DownloadFailed, bool NewChapterAvailable,
        bool ImportCompleted, bool HealthIssue);
    public record NotificationDto(
        int Id, string Name, NotificationType Type, bool Enabled, ConfigDto Config, EventsDto Events);
    public record NotificationRequest(
        string Name, NotificationType Type, bool Enabled, ConfigDto Config, EventsDto Events);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok((await db.Notifications.OrderBy(n => n.Id).ToListAsync(ct)).Select(ToDto));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] NotificationRequest request, CancellationToken ct)
    {
        if (Validate(request) is { } error)
        {
            return BadRequest(new { error });
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
        if (Validate(request) is { } error)
        {
            return BadRequest(new { error });
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
        if (Validate(request) is { } error)
        {
            return BadRequest(new { error });
        }

        var transient = new Notification();
        Apply(transient, request);
        var message = new NotificationMessage(
            NotificationEventType.Test,
            Title: "Mangarr test notification",
            Body: $"This is a test from your '{request.Name}' connection. If you can read this, it works.");

        try
        {
            await notifications.SendToAsync(transient, message, ct);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { success = false, error = ex.Message });
        }
    }

    /// <summary>Validates the URL fields required by the connection's type. Returns null when valid.</summary>
    private static string? Validate(NotificationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return "Name is required";
        }

        if (!System.Enum.IsDefined(typeof(NotificationType), request.Type))
        {
            return "Unknown notification type";
        }

        var url = request.Type == NotificationType.Discord ? request.Config.WebhookUrl : request.Config.Url;
        if (string.IsNullOrWhiteSpace(url))
        {
            return request.Type == NotificationType.Discord
                ? "Discord webhook URL is required"
                : "Webhook URL is required";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "URL must be a full http:// or https:// address";
        }

        return null;
    }

    private static void Apply(Notification entity, NotificationRequest request)
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
    }

    private static string SerializeConfig(NotificationType type, ConfigDto config) => type switch
    {
        NotificationType.Discord =>
            JsonSerializer.Serialize(new NotificationConfig.DiscordConfig(config.WebhookUrl), JsonOptions),
        NotificationType.Webhook =>
            JsonSerializer.Serialize(new NotificationConfig.WebhookConfig(config.Url, config.BearerToken), JsonOptions),
        _ => throw new System.ArgumentOutOfRangeException(nameof(type), type, "Unknown notification type")
    };

    private static NotificationDto ToDto(Notification n)
    {
        var config = n.Type == NotificationType.Discord
            ? new ConfigDto(NotificationConfig.Discord(n.ConfigJson).WebhookUrl, null, null)
            : new ConfigDto(null, NotificationConfig.Webhook(n.ConfigJson).Url,
                NotificationConfig.Webhook(n.ConfigJson).BearerToken);

        return new NotificationDto(n.Id, n.Name, n.Type, n.Enabled, config, new EventsDto(
            n.OnChapterDownloaded, n.OnDownloadFailed, n.OnNewChapterAvailable,
            n.OnImportCompleted, n.OnHealthIssue));
    }
}
