using System.Collections.Concurrent;

namespace Maki.Api.Services;

/// <summary>
/// Per-source search results from a Discover preview, held so that adding the same series soon
/// after maps its sources without searching every site a second time. Keyed by MangaBaka id and
/// taken once: a later re-match of a series in the library always searches fresh.
/// </summary>
public sealed class SourceMatchSearchCache(TimeProvider clock)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<int, Entry> _entries = new();

    internal sealed record Entry(
        SourceMatchService.MatchTarget Target,
        IReadOnlyList<SourceMatchService.SourceOutcome> Outcomes,
        DateTimeOffset StoredAt);

    internal void Store(int mangaBakaId, SourceMatchService.MatchTarget target,
        IEnumerable<SourceMatchService.SourceOutcome> outcomes)
    {
        var now = clock.GetUtcNow();
        foreach (var (id, stale) in _entries.Where(pair => now - pair.Value.StoredAt > Lifetime).ToList())
        {
            _entries.TryRemove(new KeyValuePair<int, Entry>(id, stale));
        }

        // A source that threw is left out so the add searches it again rather than trusting a blip.
        _entries[mangaBakaId] = new Entry(target, [.. outcomes.Where(o => !o.Failed)], now);
    }

    /// <summary>
    /// The stored outcomes, by source name, when they were searched for this exact target within the
    /// hour. Removes the entry either way.
    /// </summary>
    internal IReadOnlyDictionary<string, SourceMatchService.SourceOutcome>? Take(
        int mangaBakaId, SourceMatchService.MatchTarget target)
    {
        if (!_entries.TryRemove(mangaBakaId, out var entry) ||
            clock.GetUtcNow() - entry.StoredAt > Lifetime ||
            !entry.Target.SameAs(target))
        {
            return null;
        }

        return entry.Outcomes.ToDictionary(o => o.Source.Name, StringComparer.OrdinalIgnoreCase);
    }
}
