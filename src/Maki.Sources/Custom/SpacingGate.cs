using System.Collections.Concurrent;

namespace Maki.Sources.Custom;

/// <summary>
/// Holds every request of one custom source, HTML and page images alike, at least
/// <see cref="Interval"/> apart. No timer behind it, so a source that is reloaded or deleted leaves
/// nothing running, and the interval can change in place when an admin edits it.
/// </summary>
public sealed class SpacingGate(TimeSpan interval, TimeProvider? time = null)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private DateTimeOffset _next = DateTimeOffset.MinValue;

    public TimeSpan Interval { get; set; } = interval;

    public async Task WaitAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow();
            if (_next > now)
            {
                await Task.Delay(_next - now, _time, ct);
                now = _time.GetUtcNow();
            }

            _next = now + Interval;
        }
        finally
        {
            _lock.Release();
        }
    }
}

/// <summary>One <see cref="SpacingGate"/> per custom source name, kept across registry reloads.</summary>
public sealed class CustomSourceThrottle
{
    private readonly ConcurrentDictionary<string, SpacingGate> _gates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The gate a registered source runs on, set to its saved interval.</summary>
    public SpacingGate For(string sourceName, int intervalMs)
    {
        var gate = Shared(sourceName, intervalMs);
        gate.Interval = TimeSpan.FromMilliseconds(intervalMs);
        return gate;
    }

    /// <summary>
    /// The same gate for a draft, a test run or a comparison, without touching its interval: an
    /// unsaved definition must not change how fast the saved one reads the site, but it still waits
    /// its turn with it.
    /// </summary>
    public SpacingGate Shared(string sourceName, int intervalMs) =>
        _gates.GetOrAdd(sourceName, _ => new SpacingGate(TimeSpan.FromMilliseconds(intervalMs)));
}
