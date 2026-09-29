namespace Maki.Core.Quality;

/// <summary>
/// Picks which chapters to sample when measuring a series' sources side by side. Prefers chapters
/// every source lists, so each source is judged on the same material, and spreads the picks across
/// the series instead of taking the first few: early chapters are often one shared official volume
/// every site mirrors, while later ones show whose scans a source actually carries.
/// </summary>
public static class ScoutChapters
{
    /// <param name="MappingCount">How many of the sources being measured list this chapter.</param>
    public sealed record Candidate(int ChapterId, decimal? Number, int MappingCount);

    public static IReadOnlyList<int> Pick(IReadOnlyCollection<Candidate> candidates, int count, Random random)
    {
        if (count <= 0 || candidates.Count == 0)
        {
            return [];
        }

        var best = candidates.Max(c => c.MappingCount);
        var pool = candidates.Where(c => c.MappingCount == best).ToList();
        if (pool.Count < count)
        {
            pool = [.. candidates.OrderByDescending(c => c.MappingCount).ThenBy(c => c.Number ?? decimal.MaxValue).Take(count)];
        }

        pool = [.. pool.OrderBy(c => c.Number ?? decimal.MaxValue).ThenBy(c => c.ChapterId)];
        var buckets = Math.Min(count, pool.Count);
        var picks = new List<int>(buckets);
        for (var i = 0; i < buckets; i++)
        {
            picks.Add(pool[random.Next(pool.Count * i / buckets, pool.Count * (i + 1) / buckets)].ChapterId);
        }

        return picks;
    }
}
