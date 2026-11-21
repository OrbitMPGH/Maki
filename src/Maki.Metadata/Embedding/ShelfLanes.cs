namespace Maki.Metadata.Embedding;

/// <summary>
/// Gives a reader's side tastes a share of the page. Seeds are grouped by type and by Action,
/// Romance or neither; every group but the largest that holds <see cref="MinSeeds"/> seeds and
/// <see cref="RecommenderTuning.LanesMinShare"/> of the seed weight is a side taste. The whole-shelf
/// page is kept through <see cref="RecommenderTuning.LanesFrom"/>, and each side taste is topped up
/// from picks scored on its own seeds until it holds <see cref="RecommenderTuning.Lanes"/> x its
/// share of the page, counting what the page already holds. See distribution/AGENTS.md, "v9: lanes
/// as a top-up".
/// </summary>
public static class ShelfLanes
{
    /// <summary>The fewest seeds a side taste needs before it gets a lane.</summary>
    public const int MinSeeds = 3;

    public readonly record struct Key(int Type, int Bucket);

    /// <param name="Weight">The group's seed weight.</param>
    /// <param name="Total">The seed weight of every group together.</param>
    public sealed record Side(Key Key, IReadOnlyList<long> Ids, double Weight, double Total);

    public sealed record Shortfall(Side Side, int Target, int Have);

    /// <summary>A row's taste group: its type, and Action (1), else Romance (2), else neither (0).</summary>
    public static Key KeyOf(VectorIndex index, int row)
    {
        var genres = index.GenresAt(row);
        var action = index.GenreVocabulary.GetValueOrDefault("Action", -1);
        var romance = index.GenreVocabulary.GetValueOrDefault("Romance", -1);
        return new Key(index.TypeAt(row), genres.Contains(action) ? 1 : genres.Contains(romance) ? 2 : 0);
    }

    /// <summary>The side tastes of a shelf the index can place, heaviest first.</summary>
    public static IReadOnlyList<Side> Sides(
        VectorIndex index, IEnumerable<long> seeds, IReadOnlyDictionary<long, double>? weights,
        double minShare, int maxSides) =>
        Sides(
            seeds.Distinct().Select(id => (
                Id: id,
                Key: index.TryGetRow(id, out var row) ? KeyOf(index, row) : (Key?)null,
                Weight: weights?.GetValueOrDefault(id, 1.0) ?? 1.0)),
            minShare,
            maxSides);

    /// <summary>
    /// Groups seeds by key, drops the heaviest group, and keeps up to <paramref name="maxSides"/> of
    /// the rest that hold <see cref="MinSeeds"/> seeds and <paramref name="minShare"/> of the weight,
    /// heaviest first. A seed with no key or no positive weight counts toward nothing, and a repeated
    /// id counts once.
    /// </summary>
    public static IReadOnlyList<Side> Sides(
        IEnumerable<(long Id, Key? Key, double Weight)> seeds, double minShare, int maxSides)
    {
        var order = new List<Key>();
        var groups = new Dictionary<Key, (List<long> Ids, double Weight)>();
        var seen = new HashSet<long>();
        var total = 0.0;
        foreach (var (id, key, weight) in seeds)
        {
            if (weight <= 0 || key is not { } k || !seen.Add(id))
            {
                continue;
            }

            if (!groups.TryGetValue(k, out var group))
            {
                group = ([], 0.0);
                order.Add(k);
            }

            group.Ids.Add(id);
            groups[k] = (group.Ids, group.Weight + weight);
            total += weight;
        }

        return order
            .OrderByDescending(k => groups[k].Weight)
            .Skip(1)
            .Where(k => groups[k].Ids.Count >= MinSeeds && groups[k].Weight / total >= minShare)
            .Take(maxSides)
            .Select(k => new Side(k, groups[k].Ids, groups[k].Weight, total))
            .ToList();
    }

    /// <summary>
    /// The side tastes the page holds fewer of than <paramref name="ratio"/> x their share of it.
    /// With <paramref name="heldOnly"/> a taste the page holds none of is left alone.
    /// </summary>
    public static IReadOnlyList<Shortfall> Shortfalls<T>(
        IReadOnlyList<T> page, IReadOnlyList<Side> sides, Func<T, Key?> keyOf, double ratio, bool heldOnly)
    {
        var result = new List<Shortfall>();
        foreach (var side in sides)
        {
            var have = page.Count(p => keyOf(p) == side.Key);
            // Banker's rounding, as graded: shares of 7.5% and 12.5% at 0.5 of 40 both target 2.
            var target = (int)Math.Round(ratio * side.Weight / side.Total * page.Count, MidpointRounding.ToEven);
            if (target <= have || (heldOnly && have == 0))
            {
                continue;
            }

            result.Add(new Shortfall(side, target, have));
        }

        return result;
    }

    /// <summary>
    /// Inserts each queue's picks, at most its need, at slots spread evenly from
    /// <paramref name="from"/> down; the first <paramref name="from"/> picks never move. Slots are
    /// filled top down, each by the first pick of its queue that is accepted. With
    /// <paramref name="limits"/> (<see cref="ShelfFame"/> limits of the whole shelf) a pick is accepted
    /// only when the page with it placed has no more breached slots than the page as placed so far;
    /// a refused pick stays in its queue for that queue's next slot. Each accepted insert displaces
    /// the page's lowest pick outside every side taste, and a slot nothing fills displaces nothing,
    /// so every pick that stays keeps its order and with no insert the page comes back unchanged.
    /// Returns <c>page.Count</c> picks; the displaced ones are left out.
    /// </summary>
    public static List<T> Merge<T>(
        IReadOnlyList<T> page, IReadOnlySet<Key> sideKeys, IReadOnlyList<(List<T> Picks, int Need)> queues,
        Func<T, Key?> keyOf, Func<T, int> rankOf, int from, double[]? limits)
    {
        if (page.Count <= from)
        {
            return [.. page];
        }

        var droppable = Enumerable.Range(from, page.Count - from)
            .Reverse()
            .Where(i => keyOf(page[i]) is not { } key || !sideKeys.Contains(key))
            .ToList();
        var k = Math.Min(queues.Sum(q => q.Need), droppable.Count);
        if (k == 0)
        {
            return [.. page];
        }

        var served = new int[queues.Count];
        var schedule = new List<int>(k);
        while (schedule.Count < k)
        {
            var next = Enumerable.Range(0, queues.Count)
                .Where(q => served[q] < queues[q].Need)
                .OrderBy(q => (served[q] + 0.5) / queues[q].Need)
                .ThenByDescending(q => queues[q].Need)
                .First();
            schedule.Add(next);
            served[next]++;
        }

        var span = page.Count - from;
        var slots = Enumerable.Range(0, k).Select(j => from + (int)((j + 0.5) * span / k)).ToList();
        var pending = queues.Select(q => q.Picks.ToList()).ToList();
        var placed = new Dictionary<int, T>();

        List<T> Layout((int Slot, T Pick)? trial = null)
        {
            var drops = droppable.Take(placed.Count + (trial is null ? 0 : 1)).ToHashSet();
            var kept = new Queue<T>(page.Where((_, i) => !drops.Contains(i)));
            var list = new List<T>(page.Count);
            for (var position = 0; position < page.Count; position++)
            {
                list.Add(
                    trial is { } t && t.Slot == position ? t.Pick
                    : placed.TryGetValue(position, out var pick) ? pick
                    : kept.Dequeue());
            }

            return list;
        }

        int Breaches(List<T> list)
        {
            var counts = new int[ShelfFame.Thresholds.Length];
            var breaches = 0;
            for (var slot = 1; slot <= list.Count; slot++)
            {
                var rank = rankOf(list[slot - 1]);
                var over = false;
                for (var t = 0; t < counts.Length; t++)
                {
                    if (rank > 0 && rank <= ShelfFame.Thresholds[t])
                    {
                        counts[t]++;
                    }

                    over |= counts[t] > Math.Ceiling(limits![t] * slot);
                }

                breaches += over ? 1 : 0;
            }

            return breaches;
        }

        var current = limits is null ? 0 : Breaches([.. page]);
        for (var j = 0; j < k; j++)
        {
            var queue = pending[schedule[j]];
            for (var c = 0; c < queue.Count; c++)
            {
                if (limits is not null)
                {
                    var breaches = Breaches(Layout((slots[j], queue[c])));
                    if (breaches > current)
                    {
                        continue;
                    }

                    current = breaches;
                }

                placed[slots[j]] = queue[c];
                queue.RemoveAt(c);
                break;
            }
        }

        return placed.Count == 0 ? [.. page] : Layout();
    }

    /// <summary>
    /// The whole pool after <see cref="Merge"/> ran on its first <paramref name="size"/> picks: the
    /// merged page, then the picks it displaced in their old order, then the rest of
    /// <paramref name="whole"/> in its order, each id once and at most <paramref name="limit"/>.
    /// </summary>
    public static List<T> Assemble<T, TId>(
        IReadOnlyList<T> merged, IReadOnlyList<T> whole, int size, Func<T, TId> idOf, int limit)
    {
        var placed = merged.Select(idOf).ToHashSet();
        return merged
            .Concat(whole.Take(size).Where(p => !placed.Contains(idOf(p))))
            .Concat(whole.Skip(size).Where(p => !placed.Contains(idOf(p))))
            .Take(limit)
            .ToList();
    }
}
