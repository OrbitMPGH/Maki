using System.Threading.Channels;

namespace Maki.Api.Services;

/// <summary>
/// Evaluates a reader's achievements after a chapter completes, off the progress request.
/// <para>
/// The evaluation recomputes the user's whole history, so running it inside the write that finishes
/// a chapter made the reader wait for it. The controller now only enqueues the user here, after the
/// completion has been saved, so the recompute sees it. Unlocks and level-ups reach the reader
/// through the inbox push <see cref="AchievementService"/> already raises.
/// </para>
/// <para>
/// One entry per user: a user finishing several chapters before the worker gets to them is evaluated
/// once, which is also what bounds the channel to the number of distinct readers. A completion that
/// lands while that user's evaluation is running queues a second pass, since the first may have
/// read the history before it.
/// </para>
/// </summary>
public class AchievementEvaluationQueue(IServiceScopeFactory scopes, ILogger<AchievementEvaluationQueue> logger)
    : BackgroundService
{
    private readonly object _lock = new();
    private readonly HashSet<int> _pending = [];
    private readonly Channel<int> _ready = Channel.CreateUnbounded<int>(
        new UnboundedChannelOptions { SingleReader = true });

    internal int Pending
    {
        get
        {
            lock (_lock)
            {
                return _pending.Count;
            }
        }
    }

    public virtual void Enqueue(int userId)
    {
        lock (_lock)
        {
            if (!_pending.Add(userId))
            {
                return;
            }
        }

        _ready.Writer.TryWrite(userId);
    }

    /// <summary>Evaluates everything queued so far.</summary>
    internal async Task FlushAsync(CancellationToken ct)
    {
        while (_ready.Reader.TryRead(out var userId))
        {
            await EvaluateAsync(userId, ct);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var userId in _ready.Reader.ReadAllAsync(stoppingToken))
            {
                await EvaluateAsync(userId, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task EvaluateAsync(int userId, CancellationToken ct)
    {
        lock (_lock)
        {
            _pending.Remove(userId);
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<UserMetricsService>().Invalidate(userId);
            await scope.ServiceProvider.GetRequiredService<AchievementService>().EvaluateAsync(userId, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Achievement evaluation failed for user {UserId}", userId);
        }
    }
}
