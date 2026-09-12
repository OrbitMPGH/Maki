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

    /// <summary>
    /// Waits for the other builds to finish. Dispose the result to let the next one in; a
    /// cancelled wait throws, which every caller already treats as shutdown.
    /// </summary>
    public async Task<IDisposable> EnterAsync(string what, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            logger.LogDebug("{Job} is waiting for another artifact build to finish", what);
            await _gate.WaitAsync(ct);
        }

        return new Lease(_gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            // A job whose body both disposes the lease and unwinds through a catch would otherwise
            // release twice and let two builds in at once.
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }
        }
    }
}
