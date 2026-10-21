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
/// the wait is bounded in count per user and in age, and the user's access is checked again before one
/// is applied. A write that throws is tried once more and then waits the same way, since KOReader
/// already had its 200 and will not send it again. It does not depend on an index pass adding ids,
/// which a document that already resolves never gets: a timer tries it again a few times with a
/// growing delay (<see cref="RetryFailedAfter"/>, <see cref="FailedRounds"/>), and every index pass
/// tries it after that. Waiting pushes live in memory: a restart drops them.
/// </para>
/// </summary>
public sealed class KoreaderPushWriter(
    IServiceScopeFactory scopes,
    ISchedulerFactory schedulerFactory,
    TimeProvider clock,
    ILogger<KoreaderPushWriter> logger) : BackgroundService
{
    /// <summary>Waiting pushes kept per user; past it the user's oldest goes.</summary>
    public const int MaxWaiting = 500;

    private const int Attempts = 2;
    public static readonly TimeSpan WaitFor = TimeSpan.FromDays(7);

    /// <summary>The delay before a write that threw is tried again, times the rounds it has failed.</summary>
    public static readonly TimeSpan RetryFailedAfter = TimeSpan.FromMinutes(2);

    /// <summary>Timer rounds for a write that keeps throwing; past them only index passes retry it.</summary>
    public const int FailedRounds = 5;

    /// <param name="Failures">Rounds this push's write has thrown. Zero for a push that only waits for its document.</param>
    public sealed record Push(
        int UserId, string Document, string? Progress, double? Percentage, string? Device, string? DeviceId,
        DateTime ReceivedAt, int Failures = 0);

    private readonly record struct Key(int UserId, string Document);

    private readonly object _lock = new();
    private readonly Dictionary<Key, Push> _pending = [];
    private readonly Dictionary<Key, Push> _waiting = [];
    private readonly HashSet<ITimer> _timers = [];
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

    /// <summary>
    /// Queues waiting pushes again: every one after an index pass added document ids, only the writes
    /// that threw (<paramref name="failedOnly"/>) after a pass that added none.
    /// </summary>
    public void RetryWaiting(bool failedOnly = false)
    {
        List<Push> again;
        lock (_lock)
        {
            var cutoff = clock.GetUtcNow().UtcDateTime - WaitFor;
            var retried = _waiting.Where(w => !failedOnly || w.Value.Failures > 0).ToList();
            foreach (var (key, _) in retried)
            {
                _waiting.Remove(key);
            }

            again = retried.Select(w => w.Value).Where(p => p.ReceivedAt >= cutoff).ToList();
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

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await WriteOnceAsync(key, push, ct);
                return;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (attempt < Attempts)
                {
                    logger.LogDebug(e, "KOReader progress push failed for user {UserId}; trying again", push.UserId);
                    continue;
                }

                var failed = push with { Failures = push.Failures + 1 };
                logger.LogWarning(e, "KOReader progress push failed for user {UserId}; kept to try again", push.UserId);
                Wait(key, failed);
                if (failed.Failures < FailedRounds)
                {
                    RetryLater(key, failed, RetryFailedAfter * failed.Failures);
                }

                return;
            }
        }
    }

    private async Task WriteOnceAsync(Key key, Push push, CancellationToken ct)
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

    private void RetryLater(Key key, Push push, TimeSpan delay)
    {
        ITimer? timer = null;
        lock (_lock)
        {
            timer = clock.CreateTimer(_ =>
            {
                bool again;
                lock (_lock)
                {
                    _timers.Remove(timer!);
                    // Only the push this timer was set for: a newer one or a success has replaced it otherwise.
                    again = _waiting.TryGetValue(key, out var waiting) && ReferenceEquals(waiting, push);
                }

                timer!.Dispose();
                if (again)
                {
                    Enqueue(push);
                }
            }, null, delay, Timeout.InfiniteTimeSpan);
            _timers.Add(timer);
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
            var mine = _waiting.Where(w => w.Key.UserId == key.UserId).ToList();
            if (mine.Count > MaxWaiting)
            {
                _waiting.Remove(mine.MinBy(w => w.Value.ReceivedAt).Key);
            }
        }
    }
}
