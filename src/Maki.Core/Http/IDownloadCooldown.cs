namespace Maki.Core.Http;

/// <summary>
/// The per-source scraper backoff entered after that source rate-limits us. Page fetching depends on
/// this abstraction so it can honor a cooldown owned by the API's download queue without Core
/// referencing queue infrastructure.
/// </summary>
public interface IDownloadCooldown
{
    /// <summary>How long callers must still wait before touching <paramref name="sourceName"/> again.</summary>
    TimeSpan Remaining(string sourceName);

    /// <summary>Completes once <paramref name="sourceName"/>'s cooldown has elapsed; returns immediately when none is active.</summary>
    Task WaitAsync(string sourceName, CancellationToken ct = default);
}
