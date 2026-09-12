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

    /// <summary>Records a read. Called on every hand-out, cached or freshly built.</summary>
    public void Touch() => Interlocked.Exchange(ref _ticks, DateTime.UtcNow.Ticks);

    /// <summary>How long since the last hand-out.</summary>
    public TimeSpan Idle =>
        DateTime.UtcNow - new DateTime(Interlocked.Read(ref _ticks), DateTimeKind.Utc);
}
