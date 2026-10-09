namespace Maki.Metadata;

/// <summary>
/// Remembers that loading a file failed, so a cache does not re-read a corrupt multi-megabyte
/// artifact on every request. A failure stands while the file keeps the same write time and length,
/// and lapses after <see cref="Backoff"/> so a transient error (a locked file, a full disk) is
/// retried without waiting for a restart.
/// </summary>
internal sealed class LoadFailureMemo(Func<DateTime>? clock = null)
{
    public static readonly TimeSpan Backoff = TimeSpan.FromMinutes(5);

    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private volatile Failure? _failure;

    private sealed record Failure(long Ticks, long Length, DateTime At);

    public void Record(string path)
    {
        var info = new FileInfo(path);
        _failure = info.Exists ? new Failure(info.LastWriteTimeUtc.Ticks, info.Length, _clock()) : null;
    }

    public void Clear() => _failure = null;

    public bool ShouldSkip(string path)
    {
        if (_failure is not { } failure)
        {
            return false;
        }

        var info = new FileInfo(path);
        return info.Exists
            && info.LastWriteTimeUtc.Ticks == failure.Ticks
            && info.Length == failure.Length
            && _clock() - failure.At < Backoff;
    }
}
