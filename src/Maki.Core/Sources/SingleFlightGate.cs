using System.Runtime.ExceptionServices;

namespace Maki.Core.Sources;

/// <summary>
/// One cache key's fetch gate, shared by <see cref="SourceChapterListCache"/> and
/// <see cref="SourceExternalIdCache"/>: one fetch at a time, and the callers that were already
/// queued behind a failed fetch get its failure for up to <see cref="FailureTtl"/> instead of each
/// re-running a request that just timed out or was told 429. Only failures are remembered here;
/// a success lives in the owning cache.
/// </summary>
internal sealed class SingleFlightGate(TimeProvider time)
{
    public static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _failures;
    private ExceptionDispatchInfo? _failure;
    private DateTime _failedAt;

    /// <summary>A fetch is in flight or queued on this key.</summary>
    public bool Busy => _gate.CurrentCount == 0;

    /// <param name="cached">Re-checked once the gate is held: another caller may have filled it meanwhile.</param>
    public async Task<T> RunAsync<T>(
        Func<(bool Hit, T Value)> cached, Func<CancellationToken, Task<T>> fetch, CancellationToken ct)
    {
        var failuresSeen = Interlocked.Read(ref _failures);
        await _gate.WaitAsync(ct);
        try
        {
            if (cached() is (true, var value))
            {
                return value;
            }

            if (_failure is { } failure && Interlocked.Read(ref _failures) > failuresSeen &&
                time.GetUtcNow().UtcDateTime - _failedAt < FailureTtl)
            {
                Replay(failure);
            }

            try
            {
                var result = await fetch(ct);
                _failure = null;
                return result;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _failure = ExceptionDispatchInfo.Capture(ex);
                _failedAt = time.GetUtcNow().UtcDateTime;
                Interlocked.Increment(ref _failures);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A source's own timeout surfaces as a cancellation (HttpClient throws TaskCanceledException).
    /// A waiter whose token was never cancelled gets it as a <see cref="TimeoutException"/> instead,
    /// so nothing upstream mistakes it for its own cancellation.
    /// </summary>
    private static void Replay(ExceptionDispatchInfo failure)
    {
        if (failure.SourceException is OperationCanceledException cancelled)
        {
            throw new TimeoutException(cancelled.Message, cancelled);
        }

        failure.Throw();
    }
}
