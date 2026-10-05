using System.Collections.Concurrent;
using System.Text.Json;
using Maki.Api.Hubs;
using Maki.Core.Configuration;
using Maki.Core.Kavita;
using Microsoft.AspNetCore.SignalR.Client;

namespace Maki.Api.Services;

public enum KavitaLiveStatus
{
    Off,
    Connecting,
    Connected,
    NotAdmin,
    Unreachable,
}

/// <summary>
/// Opt-in (<see cref="SettingKeys.ReaderPullFromKavita"/>): holds a connection to Kavita's own
/// SignalR hub and marks a series' chapters read here as soon as Kavita reports progress on it,
/// rather than at the next scrobble tick.
/// <para>
/// Kavita fires <c>UserProgressUpdate</c> on every page turn, so events are coalesced per series and
/// handled once the series goes quiet (or after <see cref="MaxWait"/> of continuous reading). Each
/// one runs <see cref="KavitaReadImportService.MarkSeriesAsync"/>: the same rows the manual import
/// writes, and nothing else. Rewind and tracker pushes still come from the scrobble tick, which is
/// also what catches up on anything read while this was disconnected.
/// </para>
/// </summary>
public class KavitaLiveReadSync(
    SettingsService settings,
    IUserSettingsStore userSettings,
    KavitaUserResolver kavitaUser,
    KavitaClient kavita,
    KavitaReadImportService readImport,
    EventBroadcaster events,
    ILogger<KavitaLiveReadSync> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan NotAdminRetryAfter = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(10);
    private const int MaxAttempts = 3;

    internal sealed record Target(string Url, string ApiKey, int UserId);

    private readonly record struct Pending(DateTime First, DateTime Last, int Attempts = 0, DateTime NotBefore = default);

    private readonly ConcurrentDictionary<int, Pending> _pending = new();
    private readonly SemaphoreSlim _wake = new(0);
    private HubConnection? _connection;
    private DateTime _retryAt;
    private DateTime _reconcileAt;

    internal Target? Active { get; set; }

    public KavitaLiveStatus Status { get; internal set; }

    internal int PendingCount => _pending.Count;

    /// <summary>Re-reads the settings now instead of at the next check, and retries a failed connection.</summary>
    public void Nudge()
    {
        _retryAt = DateTime.MinValue;
        _reconcileAt = DateTime.MinValue;
        Wake();
    }

    internal void Enqueue(int kavitaSeriesId, DateTime now) =>
        _pending.AddOrUpdate(kavitaSeriesId, new Pending(now, now), (_, prev) => prev with { Last = now });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (DateTime.UtcNow >= _reconcileAt)
                    {
                        _reconcileAt = DateTime.UtcNow + CheckInterval;
                        await ReconcileAsync(stoppingToken);
                    }

                    await FlushAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Kavita live read sync failed");
                }

                var busy = !_pending.IsEmpty && Status == KavitaLiveStatus.Connected;
                await _wake.WaitAsync(busy ? TimeSpan.FromSeconds(1) : CheckInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await DisconnectAsync();
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        var desired = await DesiredAsync(ct);
        if (desired is null)
        {
            await DisconnectAsync();
            Active = null;
            _pending.Clear();
            Status = KavitaLiveStatus.Off;
            return;
        }

        if (desired != Active)
        {
            await DisconnectAsync();
            Active = desired;
            _pending.Clear();
            _retryAt = DateTime.MinValue;
        }

        if (_connection is { State: not HubConnectionState.Disconnected } || DateTime.UtcNow < _retryAt)
        {
            return;
        }

        await ConnectAsync(desired, ct);
    }

    private async Task<Target?> DesiredAsync(CancellationToken ct)
    {
        var url = await settings.GetAsync(SettingKeys.KavitaUrl, ct);
        var apiKey = await settings.GetAsync(SettingKeys.KavitaApiKey, ct);
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        if (await kavitaUser.ResolveAsync(ct) is not { } userId ||
            await userSettings.GetAsync(userId, SettingKeys.ReaderPullFromKavita, ct) != "true")
        {
            return null;
        }

        return new Target(url, apiKey, userId);
    }

    private async Task ConnectAsync(Target target, CancellationToken ct)
    {
        await DisconnectAsync();
        Status = KavitaLiveStatus.Connecting;

        KavitaLiveEvents.TokenIdentity? identity;
        try
        {
            identity = KavitaLiveEvents.ReadToken(
                await kavita.GetAccessTokenAsync(target.Url, target.ApiKey, force: true, ct));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("Kavita live read sync could not authenticate: {Error}", e.Message);
            Status = KavitaLiveStatus.Unreachable;
            _retryAt = DateTime.UtcNow + RetryAfter;
            return;
        }

        if (identity is null)
        {
            Status = KavitaLiveStatus.Unreachable;
            _retryAt = DateTime.UtcNow + RetryAfter;
            return;
        }

        if (!identity.Value.IsAdmin)
        {
            Status = KavitaLiveStatus.NotAdmin;
            _retryAt = DateTime.UtcNow + NotAdminRetryAfter;
            return;
        }

        var kavitaUserId = identity.Value.UserId;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(target.Url.TrimEnd('/') + "/"), "hubs/messages"), o =>
                o.AccessTokenProvider = async () =>
                    await kavita.GetAccessTokenAsync(target.Url, target.ApiKey, force: false, CancellationToken.None))
            .WithAutomaticReconnect()
            .Build();

        connection.On<JsonElement>(KavitaLiveEvents.ProgressEvent, message =>
        {
            if (KavitaLiveEvents.SeriesIdFor(message, kavitaUserId) is { } seriesId)
            {
                Enqueue(seriesId, DateTime.UtcNow);
                Wake();
            }
        });
        // Disposing a replaced connection raises Closed too, so each handler checks it is still the live one.
        connection.Reconnecting += _ =>
        {
            if (ReferenceEquals(_connection, connection))
            {
                Status = KavitaLiveStatus.Connecting;
            }

            return Task.CompletedTask;
        };
        connection.Reconnected += _ =>
        {
            if (ReferenceEquals(_connection, connection))
            {
                Status = KavitaLiveStatus.Connected;
            }

            return Task.CompletedTask;
        };
        connection.Closed += _ =>
        {
            if (ReferenceEquals(_connection, connection))
            {
                Status = KavitaLiveStatus.Unreachable;
                _retryAt = DateTime.UtcNow + RetryAfter;
                Wake();
            }

            return Task.CompletedTask;
        };

        _connection = connection;
        try
        {
            await connection.StartAsync(ct);
            Status = KavitaLiveStatus.Connected;
            logger.LogInformation("Listening to Kavita for reading progress");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("Kavita live read sync could not connect: {Error}", e.Message);
            Status = KavitaLiveStatus.Unreachable;
            _retryAt = DateTime.UtcNow + RetryAfter;
        }
    }

    internal async Task FlushAsync(CancellationToken ct, DateTime? at = null)
    {
        // A dropped connection keeps its queue: a chapter finished during the gap is still owed a mark.
        if (Active is not { } target || Status != KavitaLiveStatus.Connected)
        {
            return;
        }

        var now = at ?? DateTime.UtcNow;
        foreach (var entry in _pending)
        {
            var pending = entry.Value;
            if (now < pending.NotBefore || (now - pending.Last < Quiet && now - pending.First < MaxWait))
            {
                continue;
            }

            // Only removes the entry if no newer event touched it since it was read above.
            if (!_pending.TryRemove(entry))
            {
                continue;
            }

            try
            {
                var result = await readImport.MarkSeriesAsync(target.UserId, target.Url, target.ApiKey, entry.Key, ct);
                if (result is { Marked: > 0 } r)
                {
                    logger.LogInformation("Marked {Count} chapter(s) read from Kavita for series {SeriesId}",
                        r.Marked, r.SeriesId);
                    await events.ReadProgressChanged(target.UserId, r.SeriesId);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                var attempts = pending.Attempts + 1;
                logger.LogWarning(
                    "Could not record Kavita read state for Kavita series {KavitaSeriesId} (attempt {Attempt} of {Max}): {Error}",
                    entry.Key, attempts, MaxAttempts, e.Message);
                if (attempts < MaxAttempts)
                {
                    var retry = new Pending(pending.First, pending.Last, attempts, now + FailureBackoff);
                    _pending.AddOrUpdate(entry.Key, retry, (_, newer) => newer);
                }
            }
        }
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            _wake.Release();
        }
    }

    private async Task DisconnectAsync()
    {
        if (_connection is not { } connection)
        {
            return;
        }

        _connection = null;
        try
        {
            await connection.DisposeAsync();
        }
        catch (Exception e)
        {
            logger.LogDebug("Closing the Kavita hub connection failed: {Error}", e.Message);
        }
    }
}
