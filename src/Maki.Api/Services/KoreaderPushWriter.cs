using System.Threading.Channels;
using Maki.Api.Jobs;
using Maki.Data;
using Quartz;

namespace Maki.Api.Services;

/// <summary>
/// Applies KOReader progress pushes off the response path.
/// <para>
/// KOReader counts any status but 200 as a failed push, queues it, and stops draining that queue at
/// the first failure, so the controller answers every push 200 at once and hands it over here. A
/// push near the end of a volume completes every chapter before it, which can outlast KOReader's
/// few-second timeout; written here it is neither retried for that nor cut off halfway when the
/// device gives up. Pushes for the same user and document that arrive while one is pending fold
/// into the latest.
/// </para>
/// <para>
/// A push for a document no file resolves to yet waits, and is tried again after an index pass adds
/// document ids (<see cref="RetryWaiting"/>), so a copy taken from the library before the index got to
/// it keeps its progress. Most waiting pushes are books from somewhere else that never resolve, so
/// the wait is bounded in count and age, and the user's access is checked again before one is applied.
/// </para>
/// </summary>
public sealed class KoreaderPushWriter(
    IServiceScopeFactory scopes,
    ISchedulerFactory schedulerFactory,
    TimeProvider clock,
    ILogger<KoreaderPushWriter> logger) : BackgroundService
{
    public const int MaxWaiting = 500;
    public static readonly TimeSpan WaitFor = TimeSpan.FromDays(7);

    public sealed record Push(
        int UserId, string Document, string? Progress, double? Percentage, string? Device, string? DeviceId,
        DateTime ReceivedAt);

    private readonly record struct Key(int UserId, string Document);

    private readonly object _lock = new();
    private readonly Dictionary<Key, Push> _pending = [];
    private readonly Dictionary<Key, Push> _waiting = [];
    private readonly Channel<Key> _ready = Channel.CreateUnbounded<Key>(
        new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(int userId, string document, string? progress, double? percentage, string? device,
        string? deviceId) =>
        Enqueue(new Push(userId, document.Trim().ToLowerInvariant(), progress, percentage, device, deviceId,
            clock.GetUtcNow().UtcDateTime));

    private void Enqueue(Push push)
    {
        var key = new Key(push.UserId, push.Document);
        lock (_lock)
        {
            _waiting.Remove(key);
            var queued = _pending.ContainsKey(key);
            _pending[key] = push;
            if (queued)
            {
                return;
            }
        }

        _ready.Writer.TryWrite(key);
    }

    /// <summary>Queues every waiting push again. Called after an index pass added document ids.</summary>
    public void RetryWaiting()
    {
        List<Push> again;
        lock (_lock)
        {
            var cutoff = clock.GetUtcNow().UtcDateTime - WaitFor;
            again = _waiting.Values.Where(p => p.ReceivedAt >= cutoff).ToList();
            _waiting.Clear();
        }

        foreach (var push in again)
        {
            Enqueue(push);
        }
    }

    internal int WaitingCount
    {
        get
        {
            lock (_lock)
            {
                return _waiting.Count;
            }
        }
    }

    /// <summary>Writes everything queued so far.</summary>
    internal async Task FlushAsync(CancellationToken ct)
    {
        while (_ready.Reader.TryRead(out var key))
        {
            await WriteAsync(key, ct);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var key in _ready.Reader.ReadAllAsync(stoppingToken))
            {
                // Not stoppingToken: the push is already out of the pending map, and a volume cut off
                // halfway would leave some of its chapters completed and the rest not.
                await WriteAsync(key, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        await FlushAsync(CancellationToken.None);
    }

    private async Task WriteAsync(Key key, CancellationToken ct)
    {
        Push? push;
        lock (_lock)
        {
            if (!_pending.Remove(key, out push))
            {
                return;
            }
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;
            if (await services.GetRequiredService<OpdsAccessService>().ForUserAsync(push.UserId, ct) is not { } access)
            {
                return;
            }

            services.GetRequiredService<DataScope>().SetUser(access.UserId, access.AllRootFolders);
            var written = await services.GetRequiredService<KoreaderSyncService>().PushAsync(push.UserId,
                push.Document, push.Progress, push.Percentage, push.Device, push.DeviceId, ct);
            if (written is not null)
            {
                return;
            }

            Wait(key, push);
            var indexer = services.GetRequiredService<KoreaderDocumentIndexer>();
            await KoreaderDocumentIndexJob.TriggerSoonAsync(schedulerFactory, logger,
                () => indexer.AnyUnindexedAsync(ct));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "KOReader progress push failed for user {UserId}", push.UserId);
        }
    }

    private void Wait(Key key, Push push)
    {
        lock (_lock)
        {
            // A newer push for the same document is already queued and supersedes this one.
            if (_pending.ContainsKey(key))
            {
                return;
            }

            _waiting[key] = push;
            if (_waiting.Count > MaxWaiting)
            {
                _waiting.Remove(_waiting.MinBy(w => w.Value.ReceivedAt).Key);
            }
        }
    }
}
