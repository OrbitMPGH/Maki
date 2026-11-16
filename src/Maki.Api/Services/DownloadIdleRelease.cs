namespace Maki.Api.Services;

/// <summary>
/// Decides when a finished burst of downloads hands its memory back. A burst leaves 15 to 20 MB of
/// garbage and freed native heap that Workstation GC never collects on an idle process (measured: 29 MB
/// of managed garbage still there four and a half minutes later). Due once, after work was handed out
/// and the queue has then stayed idle for <see cref="Quiet"/>, so the short gaps between items a user
/// adds one after another never pay for a blocking collect.
/// </summary>
internal sealed class DownloadIdleRelease
{
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(2);

    private long _seen;
    private bool _pending;
    private DateTime? _idleSince;

    /// <param name="handedOut"><see cref="DownloadQueueService.HandedOut"/>.</param>
    /// <param name="busy"><see cref="DownloadQueueService.Busy"/>.</param>
    public bool Due(long handedOut, bool busy, DateTime now)
    {
        if (handedOut != _seen)
        {
            _seen = handedOut;
            _pending = true;
            _idleSince = null;
            return false;
        }

        if (busy || !_pending)
        {
            _idleSince = null;
            return false;
        }

        _idleSince ??= now;
        if (now - _idleSince < Quiet)
        {
            return false;
        }

        _pending = false;
        _idleSince = null;
        return true;
    }
}
