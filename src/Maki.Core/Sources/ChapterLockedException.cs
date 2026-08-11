namespace Maki.Core.Sources;

/// <summary>
/// Raised when a source's per-chapter page endpoint reports the chapter is still behind an
/// early-access/premium window (pages not yet public even though the chapter is listed).
/// Unlike a generic "no pages" result this isn't a broken chapter or a dead source, so the
/// download pipeline retries it quietly instead of burning it down to a permanent failure.
/// </summary>
public class ChapterLockedException(string message, DateTimeOffset? unlockAt = null) : Exception(message)
{
    /// <summary>When the source says early access ends, if it told us. Null means "unknown, use a default backoff".</summary>
    public DateTimeOffset? UnlockAt { get; } = unlockAt;
}
