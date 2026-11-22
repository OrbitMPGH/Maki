using Maki.Core.Io;

namespace Maki.Api.Services;

/// <summary>
/// Hands a background worker's memory back once it has stayed idle for <see cref="Quiet"/> after work,
/// instead of each time its loop runs dry: a bulk download wakes a worker hundreds of times, and every
/// <see cref="NativeHeap.CollectAndTrim"/> is a blocking compacting collect that pauses reader requests.
/// </summary>
internal sealed class DeferredCollect : IDisposable
{
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(2);

    private readonly Action _collect;
    private readonly TimeSpan _quiet;
    private readonly Timer _timer;
    private readonly Lock _sync = new();
    private long _epoch;
    private long _armed = -1;
    private bool _pending;

    public DeferredCollect(Action? collect = null, TimeSpan? quiet = null)
    {
        _collect = collect ?? NativeHeap.CollectAndTrim;
        _quiet = quiet ?? Quiet;
        _timer = new Timer(_ => Fire());
    }

    /// <summary>The worker woke up: a collect waiting for quiet waits again.</summary>
    public void Busy()
    {
        lock (_sync)
        {
            _epoch++;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>The worker did something since it last went idle.</summary>
    public void Worked()
    {
        lock (_sync)
        {
            _pending = true;
        }
    }

    /// <summary>The worker is about to wait: collect after <see cref="Quiet"/> unless it is busy again first.</summary>
    public void Idle()
    {
        lock (_sync)
        {
            if (_pending)
            {
                _armed = _epoch;
                _timer.Change(_quiet, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Fire()
    {
        lock (_sync)
        {
            if (!_pending || _armed != _epoch)
            {
                return;
            }

            _pending = false;
        }

        _collect();
    }

    public void Dispose() => _timer.Dispose();
}
