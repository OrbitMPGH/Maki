using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Maki.Data;
using Microsoft.EntityFrameworkCore;
using LibSubscription = Lib.Net.Http.WebPush.PushSubscription;

namespace Maki.Api.Services;

/// <summary>
/// One push for one user, already rendered in their language. <paramref name="Url"/> is the in-app
/// path a click opens; <paramref name="Tag"/> collapses repeats (<c>series-{id}</c>) on the device and,
/// as the push Topic, at the push service while the device is offline. <paramref name="Devices"/>, when
/// set, is sent to instead of the user's stored subscriptions: an alert about a revocation goes to the
/// devices that revocation just forgot.
/// </summary>
public sealed record WebPushNotification(
    int UserId, string Title, string Body, string Url, string? Tag, IReadOnlyList<PushDevice>? Devices = null);

/// <summary>What a push needs to reach one device, detached from its subscription row.</summary>
public sealed record PushDevice(string Endpoint, string P256dh, string Auth);

/// <summary>
/// Delivers Web Push to every device a user subscribed. Raises enqueue and return at once; one
/// background consumer sends, so <see cref="InboxService"/> never waits on a push service.
/// <para>
/// A 404 or 410 from the push service means the browser dropped the subscription, and the row is
/// deleted. Any other failure counts against the row, which goes at <see cref="MaxFailures"/> in a row.
/// Logged at Debug only: a stale phone is routine, not something to warn an admin about.
/// </para>
/// </summary>
public class WebPushSender(
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClients,
    WebPushKeys keys,
    TimeProvider time,
    ILogger<WebPushSender> logger) : BackgroundService
{
    public const string HttpClientName = "webpush";

    /// <summary>The VAPID contact. Push services want a way to reach whoever runs the sender.</summary>
    private const string Subject = "https://github.com/OrbitMPGH/Maki";

    internal const int MaxFailures = 5;

    private const int Capacity = 256;

    private static readonly int TimeToLiveSeconds = (int)TimeSpan.FromHours(24).TotalSeconds;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Channel<WebPushNotification> _queue = Channel.CreateBounded<WebPushNotification>(
        new BoundedChannelOptions(Capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    /// <summary>
    /// Forgets every device the user subscribed. Called wherever other sessions are revoked: a
    /// subscription is tied to the browser session, so a revoked device must stop receiving pushes too.
    /// Returns the devices it forgot, so the security alert about the revocation can still reach them.
    /// </summary>
    public static async Task<IReadOnlyList<PushDevice>> DropSubscriptionsAsync(MakiDbContext db, int userId, CancellationToken ct)
    {
        var rows = db.PushSubscriptions.IgnoreQueryFilters().Where(s => s.UserId == userId);
        var devices = await rows.Select(s => new PushDevice(s.Endpoint, s.P256dh, s.Auth)).ToListAsync(ct);
        await rows.ExecuteDeleteAsync(ct);
        return devices;
    }

    /// <summary>Queues a push. A full queue drops it: a burst that large is a backlog nobody wants on their lock screen.</summary>
    public virtual void Enqueue(WebPushNotification notification)
    {
        if (!_queue.Writer.TryWrite(notification))
        {
            logger.LogDebug("Push queue is full; dropped a push for user {UserId}", notification.UserId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var notification in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await SendAsync(notification, stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug(e, "Push delivery for user {UserId} failed", notification.UserId);
            }
        }
    }

    /// <summary>
    /// Sends now, to every subscription the user holds, or to <see cref="WebPushNotification.Devices"/>
    /// when the push names them. Returns how many took it and how many did not.
    /// </summary>
    public async Task<(int Sent, int Failed)> SendAsync(WebPushNotification notification, CancellationToken ct)
    {
        // An unrestricted scope, so any user's rows load and delete without a filter bypass.
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
        var subscriptions = notification.Devices is null
            ? await db.PushSubscriptions.Where(s => s.UserId == notification.UserId).ToListAsync(ct)
            : [];
        var devices = notification.Devices
            ?? subscriptions.Select(s => new PushDevice(s.Endpoint, s.P256dh, s.Auth)).ToList();
        if (devices.Count == 0)
        {
            return (0, 0);
        }

        var (publicKey, privateKey) = await keys.GetAsync(ct);
        using var vapid = new VapidAuthentication(publicKey, privateKey) { Subject = Subject };
        var client = new PushServiceClient(httpClients.CreateClient(HttpClientName)) { AutoRetryAfter = false };
        var payload = JsonSerializer.Serialize(new
        {
            title = notification.Title,
            body = notification.Body,
            url = notification.Url,
            tag = notification.Tag,
        }, Json);

        var now = time.GetUtcNow().UtcDateTime;
        var sent = 0;
        var failed = 0;
        for (var i = 0; i < devices.Count; i++)
        {
            // Null for a device a revocation already forgot: there is no row left to keep score on.
            var subscription = i < subscriptions.Count ? subscriptions[i] : null;
            var target = new LibSubscription { Endpoint = devices[i].Endpoint };
            target.SetKey(PushEncryptionKeyName.P256DH, devices[i].P256dh);
            target.SetKey(PushEncryptionKeyName.Auth, devices[i].Auth);
            var message = new PushMessage(payload)
            {
                TimeToLive = TimeToLiveSeconds,
                Topic = notification.Tag,
                Urgency = PushMessageUrgency.Normal,
            };

            try
            {
                await client.RequestPushMessageDeliveryAsync(target, message, vapid, VapidAuthenticationScheme.Vapid, ct);
                if (subscription is not null)
                {
                    subscription.LastSuccessAt = now;
                    subscription.FailureCount = 0;
                }

                sent++;
            }
            catch (PushServiceClientException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                logger.LogDebug("Push subscription {Id} is gone ({Status}), removing it", subscription?.Id, (int)e.StatusCode);
                if (subscription is not null)
                {
                    db.PushSubscriptions.Remove(subscription);
                }

                failed++;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug(e, "Push to subscription {Id} failed", subscription?.Id);
                if (subscription is not null && ++subscription.FailureCount >= MaxFailures)
                {
                    db.PushSubscriptions.Remove(subscription);
                }

                failed++;
            }
        }

        await db.SaveChangesAsync(ct);
        return (sent, failed);
    }
}
