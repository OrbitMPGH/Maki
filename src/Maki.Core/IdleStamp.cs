namespace Maki.Core;

/// <summary>
/// When a cached artifact was last handed to a caller, so an idle instance can give its memory
/// back.
///
/// <para>
/// Shared by the artifact caches and the headless browsers because the bookkeeping is identical in
/// all of them and getting it subtly different in one is the kind of thing nothing would notice: a
/// holder that forgot to stamp on its fast path would look idle while it was being used on every
/// request, and would then reload on a timer forever.
/// </para>
/// </summary>
public sealed class IdleStamp
{
    private long _ticks = DateTime.UtcNow.Ticks;
    private int _touched;

    /// <summary>Records a read. Called on every hand-out, cached or freshly built.</summary>
    public void Touch()
    {
        Interlocked.Exchange(ref _ticks, DateTime.UtcNow.Ticks);
        Volatile.Write(ref _touched, 1);
    }

    /// <summary>How long since the last hand-out, or since construction when there was none.</summary>
    public TimeSpan Idle =>
        DateTime.UtcNow - new DateTime(Interlocked.Read(ref _ticks), DateTimeKind.Utc);

    /// <summary>
    /// Whether anything was handed out within <paramref name="window"/>. Unlike <see cref="Idle"/>,
    /// false for a holder nothing has read yet, so a fresh process does not count as recent use.
    /// </summary>
    public bool UsedWithin(TimeSpan window) => Volatile.Read(ref _touched) == 1 && Idle < window;
}
