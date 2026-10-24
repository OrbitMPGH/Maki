using System.Buffers.Text;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Http;
using Maki.Core.Inbox;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

/// <summary>The shape <c>PushSubscription.toJSON()</c> produces in the browser, so the client can post it as is.</summary>
public record PushSubscriptionRequest(string? Endpoint, PushSubscriptionKeys? Keys);

public record PushSubscriptionKeys(string? P256dh, string? Auth);

public record PushEndpointRequest(string? Endpoint);

/// <summary>Inbox event type keys, as in the inbox prefs blob.</summary>
public record PushPrefsRequest(string[]? Types);

/// <summary>
/// Web Push for the signed-in user's devices: the VAPID key a browser subscribes with, the
/// subscriptions themselves, a test send, and which inbox events are pushed.
/// </summary>
[ApiController]
[Route("api/v1/push")]
public class PushController(
    MakiDbContext db,
    ILocalizer localizer,
    ICurrentUser currentUser,
    IUserSettings userSettings,
    WebPushKeys keys,
    WebPushSender sender,
    TimeProvider time) : ControllerBase
{
    private const int MaxEndpointLength = 2048;
    private const int MaxUserAgentLength = 256;

    /// <summary>Devices per user. The sender works through a user's rows one at a time, so an unbounded list stalls everyone's pushes.</summary>
    internal const int MaxSubscriptionsPerUser = 20;

    [HttpGet("public-key")]
    public async Task<IActionResult> PublicKey(CancellationToken ct) =>
        Ok(new { publicKey = (await keys.GetAsync(ct)).PublicKey });

    /// <summary>
    /// Registers this device, or takes it over when another account registered the same browser
    /// before: the endpoint names the browser, and pushes belong to whoever is signed in on it now.
    /// </summary>
    [HttpPost("subscription")]
    public async Task<IActionResult> Subscribe([FromBody] PushSubscriptionRequest request, CancellationToken ct)
    {
        if (!IsValid(request, out var endpoint, out var p256dh, out var auth))
        {
            return this.Fail(localizer, "error.push.invalidSubscription");
        }

        var userAgent = Request.Headers.UserAgent.ToString();
        if (userAgent.Length > MaxUserAgentLength)
        {
            userAgent = userAgent[..MaxUserAgentLength];
        }

        try
        {
            await UpsertAsync(endpoint, p256dh, auth, userAgent, ct);
        }
        catch (DbUpdateException e) when (DbErrors.IsUniqueViolation(e))
        {
            // Two tabs of one browser registering at once; the second now finds the first's row.
            db.ChangeTracker.Clear();
            await UpsertAsync(endpoint, p256dh, auth, userAgent, ct);
        }

        return Ok(new { subscribed = true });
    }

    private async Task UpsertAsync(string endpoint, string p256dh, string auth, string userAgent, CancellationToken ct)
    {
        var row = await db.PushSubscriptions.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Endpoint == endpoint, ct);
        if (row is null)
        {
            row = new PushSubscription { Endpoint = endpoint, CreatedAt = time.GetUtcNow().UtcDateTime };
            db.PushSubscriptions.Add(row);
        }

        row.UserId = currentUser.UserId;
        row.P256dh = p256dh;
        row.Auth = auth;
        row.UserAgent = string.IsNullOrEmpty(userAgent) ? null : userAgent;
        row.FailureCount = 0;
        await db.SaveChangesAsync(ct);

        // The device registering now always stays; the oldest of the others make room.
        var surplus = await db.PushSubscriptions
            .Where(s => s.UserId == currentUser.UserId && s.Id != row.Id)
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
            .Skip(MaxSubscriptionsPerUser - 1)
            .Select(s => s.Id)
            .ToListAsync(ct);
        if (surplus.Count > 0)
        {
            await db.PushSubscriptions.Where(s => surplus.Contains(s.Id)).ExecuteDeleteAsync(ct);
        }
    }

    /// <summary>Removes this device. Only the caller's own rows match, and an unknown endpoint is not an error.</summary>
    [HttpDelete("subscription")]
    public async Task<IActionResult> Unsubscribe([FromBody] PushEndpointRequest request, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(request.Endpoint))
        {
            await db.PushSubscriptions.Where(s => s.Endpoint == request.Endpoint).ExecuteDeleteAsync(ct);
        }

        return NoContent();
    }

    /// <summary>Sends a test push to every device the caller subscribed, now rather than through the queue.</summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test(CancellationToken ct)
    {
        if (!await db.PushSubscriptions.AnyAsync(ct))
        {
            return this.NotFoundMessage(localizer, "error.push.noSubscription");
        }

        var (sent, failed, error) = await sender.SendAsync(new WebPushNotification(
            currentUser.UserId,
            localizer.Get("inbox.pushTest.title"),
            localizer.Get("inbox.pushTest.body"),
            "/settings",
            "push-test"), ct);
        return Ok(new { sent, failed, error });
    }

    [HttpGet("prefs")]
    public async Task<IActionResult> GetPrefs(CancellationToken ct) =>
        Ok(new { types = PushPrefs.Keys(PushPrefs.Parse(await userSettings.GetAsync(SettingKeys.NotificationsPush, ct))) });

    /// <summary>
    /// Unknown keys are dropped, and so are admin-only types for a non-admin, as the inbox prefs do:
    /// those are never delivered to them, so storing one would only make the card disagree with what arrives.
    /// </summary>
    [HttpPut("prefs")]
    public async Task<IActionResult> SavePrefs([FromBody] PushPrefsRequest request, CancellationToken ct)
    {
        var types = PushPrefs.FromKeys(request.Types ?? [])
            .Where(t => currentUser.Has(MakiPermission.Admin) || !InboxEventTypes.IsAdminOnly(t))
            .ToHashSet();
        await userSettings.SetAsync(SettingKeys.NotificationsPush, PushPrefs.Serialize(types), ct);
        return Ok(new { types = PushPrefs.Keys(types) });
    }

    private static bool IsValid(PushSubscriptionRequest request, out string endpoint, out string p256dh, out string auth)
    {
        endpoint = request.Endpoint?.Trim() ?? string.Empty;
        p256dh = request.Keys?.P256dh?.Trim() ?? string.Empty;
        auth = request.Keys?.Auth?.Trim() ?? string.Empty;

        // The server POSTs to this URL, so it is held to public https addresses like any other
        // URL a client hands the server to fetch.
        return endpoint.Length <= MaxEndpointLength &&
               Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               PublicAddressGuard.IsAllowedUrl(uri) &&
               DecodesTo(p256dh, 65) &&
               DecodesTo(auth, 16);
    }

    private static bool DecodesTo(string base64Url, int length)
    {
        try
        {
            return base64Url.Length is > 0 and < 256 && Base64Url.DecodeFromChars(base64Url.TrimEnd('=')).Length == length;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
