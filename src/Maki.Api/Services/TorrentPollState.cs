namespace Maki.Api.Services;

/// <summary>
/// Lets <see cref="Jobs.CompletedDownloadJob"/> skip its queue read while no torrent row is in
/// flight. It runs every 15 seconds, and on an instance that has no torrents, or none right now,
/// every run read the queue to find nothing.
/// <para>
/// A grab (<see cref="ReleaseService"/>) is the only place a torrent row is created, and a retry
/// (<c>QueueController.Retry</c>) the only place a failed one comes back, so those two end the idle
/// state. The queue is still read every <see cref="RecheckWhileIdle"/>, so a path that revives one
/// some other way only delays its import.
/// </para>
/// </summary>
public sealed class TorrentPollState(TimeProvider time)
{
    public static readonly TimeSpan RecheckWhileIdle = TimeSpan.FromMinutes(5);

    private long _generation;
    private long _idleGeneration = -1;
    private long _idleSinceTicks;

    /// <summary>Taken before reading the queue, and handed to <see cref="MarkIdle"/> after.</summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>True when the last read found nothing and no grab has happened since.</summary>
    public bool CanSkip =>
        Interlocked.Read(ref _idleGeneration) == Interlocked.Read(ref _generation) &&
        time.GetUtcNow().UtcTicks - Interlocked.Read(ref _idleSinceTicks) < RecheckWhileIdle.Ticks;

    /// <summary>
    /// Records an empty read. Ignored when a grab landed after <paramref name="generation"/> was
    /// taken, since the read may have missed its row.
    /// </summary>
    public void MarkIdle(long generation)
    {
        Interlocked.Exchange(ref _idleSinceTicks, time.GetUtcNow().UtcTicks);
        Interlocked.Exchange(ref _idleGeneration, generation);
    }

    public void TorrentAdded() => Interlocked.Increment(ref _generation);
}
