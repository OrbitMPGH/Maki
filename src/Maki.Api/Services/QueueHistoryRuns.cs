using Maki.Core.Entities;

namespace Maki.Api.Services;

/// <summary>
/// Folds the download history into runs: rows of one series, one status and one kind, each no more
/// than <see cref="Gap"/> older than the run's oldest row so far. Rows of other series may land in
/// between (parallel workers interleave), so a run is keyed per series rather than by adjacency.
/// Upgrades run apart from plain downloads, and a chapterless torrent grab is always a run of one.
/// </summary>
public static class QueueHistoryRuns
{
    public static readonly TimeSpan Gap = TimeSpan.FromMinutes(30);

    public enum Kind
    {
        Download,
        Upgrade,
        Alone,
    }

    public sealed record Row(int Id, int SeriesId, QueueStatus Status, DateTime At, Kind Kind);

    /// <summary>Runs newest first, each a list of row ids newest first. <paramref name="rows"/> must be newest first.</summary>
    public static List<List<int>> Group(IReadOnlyList<Row> rows)
    {
        var runs = new List<List<int>>();
        var open = new Dictionary<(int, QueueStatus, Kind), (List<int> Ids, DateTime Oldest)>();
        foreach (var row in rows)
        {
            if (row.Kind == Kind.Alone)
            {
                runs.Add([row.Id]);
                continue;
            }

            var key = (row.SeriesId, row.Status, row.Kind);
            if (open.TryGetValue(key, out var run) && run.Oldest - row.At <= Gap)
            {
                run.Ids.Add(row.Id);
                open[key] = (run.Ids, row.At);
                continue;
            }

            var ids = new List<int> { row.Id };
            runs.Add(ids);
            open[key] = (ids, row.At);
        }
        return runs;
    }
}
