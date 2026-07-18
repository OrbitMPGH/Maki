namespace Maki.Core.Http;

/// <summary>
/// The queue-wide scraper backoff entered after a source rate-limits us. Page fetching depends on
/// this abstraction so it can honor a cooldown owned by the API's download queue without Core
/// referencing queue infrastructure.
/// </summary>
public interface IDownloadCooldown
{
    /// <summary>How long callers must still wait before touching a source again.</summary>
    TimeSpan Remaining();

    /// <summary>Completes once the cooldown has elapsed; returns immediately when none is active.</summary>
    Task WaitAsync(CancellationToken ct = default);
}
