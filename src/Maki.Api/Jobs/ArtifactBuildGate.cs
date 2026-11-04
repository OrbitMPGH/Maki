using System.Runtime;
using Maki.Core.Io;

namespace Maki.Api.Jobs;

/// <summary>
/// Lets one heavy artifact build run at a time.
///
/// <para>
/// The startup schedule fires seven of them inside the first seven minutes: the dump refresh and
/// its browse indexes, the four downloaded artifacts, and the discover warm-up, which alone builds
/// the search vectors and then scans the dump for the catalogue indexes. Quartz runs jobs
/// concurrently, so all of that used to overlap, and peak memory was the sum of every build's churn
/// rather than the largest one's. That peak is the worst moment in an instance's life and it lands
/// on a NAS three minutes after the user started the container.
/// </para>
///
/// <para>
/// Serialising them costs wall-clock time on a cold start and nothing else: none of these is on a
/// request path, and the caches they fill are built lazily anyway if a request beats them to it.
/// </para>
///
/// <para>
/// The gate is deliberately not a Quartz concurrency setting. <c>MaxConcurrency</c> would throttle
/// the download workers and the fifteen-second completed-download poll alongside the builds, which
/// is the opposite of what an instance doing both at once wants.
/// </para>
/// </summary>
public sealed class ArtifactBuildGate(ILogger<ArtifactBuildGate> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _sync = new();
    private TaskCompletionSource _idle = Completed();
    private int _waiting;
    private int _built;
    private int _collections;

    internal int Collections => Volatile.Read(ref _collections);

    /// <summary>
    /// Completes once no artifact build holds the gate. Page conversion and health verifies wait on
    /// it between files, so a dump install (a whole-file write plus index pass) never stacks with an
    /// encoder or a verify. They are not artifact builds themselves: holding the gate for a whole
    /// conversion pass would stall every build behind it for hours.
    /// </summary>
    public bool IsIdle
    {
        get
        {
            lock (_sync)
            {
                return _idle.Task.IsCompleted;
            }
        }
    }

    public Task WaitUntilIdleAsync(CancellationToken ct)
    {
        Task idle;
        lock (_sync)
        {
            idle = _idle.Task;
        }

        return idle.WaitAsync(ct);
    }

    private static TaskCompletionSource Completed()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }

    /// <summary>
    /// Waits for the other builds to finish. Dispose the result to let the next one in; a
    /// cancelled wait throws, which every caller already treats as shutdown. Call
    /// <see cref="BuildLease.MarkBuilt"/> when the run actually built or installed something, so a
    /// check that found the artifact current does not pay for a collection.
    /// </summary>
    public async Task<BuildLease> EnterAsync(string what, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            Interlocked.Increment(ref _waiting);
            try
            {
                logger.LogDebug("{Job} is waiting for another artifact build to finish", what);
                await _gate.WaitAsync(ct);
            }
            finally
            {
                Interlocked.Decrement(ref _waiting);
            }
        }

        lock (_sync)
        {
            if (_idle.Task.IsCompleted)
            {
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        return new BuildLease(this);
    }

    /// <summary>
    /// Hands the gate on, and collects first when a build has finished since the last collection
    /// and this was the last one in the queue.
    ///
    /// <para>
    /// An index build churns through far more heap than it keeps, and most of that churn is large
    /// arrays. The large object heap is not compacted by default, so what it leaves behind is holes
    /// the process keeps: measured six minutes into a start on a NAS, 84 MB of the 245 MB managed
    /// heap was fragmentation, on top of 157 MB of large objects. Nothing would have compacted that
    /// until the next idle unload, then an hour later at the earliest.
    /// </para>
    ///
    /// <para>
    /// A forced blocking compaction is normally the wrong tool. It is the right one at exactly this
    /// point: the build that just finished is the expensive thing, the queue behind it is empty, and
    /// the alternative is carrying its holes until the next unload. Skipped when another build is
    /// waiting - it would only have to be done again, and the pending flag carries to whichever
    /// build is last.
    /// </para>
    /// </summary>
    private void Release()
    {
        if (Volatile.Read(ref _waiting) == 0 && Interlocked.Exchange(ref _built, 0) == 1)
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            // Aggressive, so the freed regions are decommitted rather than kept for the next build.
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            NativeHeap.Trim();
            Interlocked.Increment(ref _collections);
        }

        if (Volatile.Read(ref _waiting) == 0)
        {
            lock (_sync)
            {
                _idle.TrySetResult();
            }
        }

        _gate.Release();
    }

    public sealed class BuildLease : IDisposable
    {
        private readonly ArtifactBuildGate _owner;
        private int _released;

        internal BuildLease(ArtifactBuildGate owner) => _owner = owner;

        public void MarkBuilt() => Volatile.Write(ref _owner._built, 1);

        public void Dispose()
        {
            // A job whose body both disposes the lease and unwinds through a catch would otherwise
            // release twice and let two builds in at once.
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _owner.Release();
            }
        }
    }
}
