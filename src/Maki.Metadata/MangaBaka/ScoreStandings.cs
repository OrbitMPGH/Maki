using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Maki.Metadata.MangaBaka;

/// <summary>
/// Where one series' score sits among the popular series of its type: the share of that group it
/// scores above, the group's size, and how the group's scores spread from 5 to 10 in half-point
/// bins. <paramref name="Group"/> is the dump's type, or <see cref="ScoreStandings.AllGroup"/> when
/// the type has too few popular series to compare against on its own.
/// </summary>
public sealed record ScoreStanding(string Group, int GroupSize, double Percentile, IReadOnlyList<int> Histogram);

/// <summary>
/// Share of all popular series (the <see cref="ScoreStandings.AllGroup"/> group) scoring at or above
/// each whole point of the dump's 0-100 scale, as a percentage: <c>AtOrAbove[70]</c> is the share
/// rated 7.0 or more. Labels the minimum-rating filter in the same terms the detail card uses.
/// </summary>
public sealed record ScoreShares(int GroupSize, IReadOnlyList<double> AtOrAbove);

/// <summary>
/// Puts a MangaBaka score in context for the Discover detail card. Scores cluster tightly (half of
/// all rated series sit between 6.0 and 6.9), so the number alone hardly separates a good series
/// from an ordinary one.
/// <para>
/// The comparison group is the most popular <see cref="PopularShare"/> of each type, ranked by the
/// dump's popularity within that type. Popularity stands in for a rating count, which the dump does
/// not carry: the long tail of barely rated series piles up around 6.0 to 6.5 and would make any
/// typical score look strong. Taking the same share of every type keeps the bar comparable between
/// types, and a type too small for that falls back to all popular series together.
/// </para>
/// <para>
/// Built once per dump file and kept until the file changes, the same stamp the catalogue indexes
/// use. One scan of the browse table, a few hundred milliseconds on the full dump.
/// </para>
/// </summary>
public class ScoreStandings(MangaBakaDumpOptions options, int minGroupSize = ScoreStandings.MinGroupSize)
{
    public const double PopularShare = 0.2;
    public const int MinGroupSize = 1000;
    public const string AllGroup = "all";

    /// <summary>Half-point bins from 5.0 to 10.0. Scores under 5 are counted in the group but drawn in no bin.</summary>
    public const int Bins = 10;

    private sealed record Snapshot(
        IReadOnlyDictionary<string, double[]> Groups,
        IReadOnlyDictionary<string, int[]> Histograms,
        long StampTicks,
        long StampLength);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private Snapshot? _snapshot;

    public virtual async Task<ScoreStanding?> GetAsync(string? type, double? rating, CancellationToken ct = default)
    {
        if (rating is not double score)
        {
            return null;
        }

        var snapshot = await LoadAsync(ct);
        if (snapshot is null)
        {
            return null;
        }

        var group = type is not null && snapshot.Groups.ContainsKey(type) ? type : AllGroup;
        if (!snapshot.Groups.TryGetValue(group, out var sorted) || sorted.Length == 0)
        {
            return null;
        }

        var below = LowerBound(sorted, score);
        return new ScoreStanding(group, sorted.Length, 100.0 * below / sorted.Length, snapshot.Histograms[group]);
    }

    public virtual async Task<ScoreShares?> GetSharesAsync(CancellationToken ct = default)
    {
        var snapshot = await LoadAsync(ct);
        if (snapshot is null || !snapshot.Groups.TryGetValue(AllGroup, out var sorted) || sorted.Length == 0)
        {
            return null;
        }

        var shares = new double[101];
        for (var point = 0; point <= 100; point++)
        {
            shares[point] = 100.0 * (sorted.Length - LowerBound(sorted, point)) / sorted.Length;
        }

        return new ScoreShares(sorted.Length, shares);
    }

    private async Task<Snapshot?> LoadAsync(CancellationToken ct)
    {
        var info = new FileInfo(options.DatabasePath);
        if (!info.Exists)
        {
            return null;
        }

        if (_snapshot is { } cached && Matches(cached, info))
        {
            return cached;
        }

        await _lock.WaitAsync(ct);
        try
        {
            info.Refresh();
            if (!info.Exists)
            {
                return null;
            }

            if (_snapshot is { } raced && Matches(raced, info))
            {
                return raced;
            }

            var built = await Task.Run(() => Build(info), ct);
            _snapshot = built;
            return built;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static bool Matches(Snapshot snapshot, FileInfo info) =>
        info.LastWriteTimeUtc.Ticks == snapshot.StampTicks && info.Length == snapshot.StampLength;

    private Snapshot Build(FileInfo info)
    {
        var byType = new Dictionary<string, List<(long Rank, double Rating)>>(StringComparer.Ordinal);
        using (var conn = new SqliteConnection($"Data Source={options.DatabasePath};Mode=ReadOnly;Pooling=False"))
        {
            conn.Open();
            var source = MangaBakaDumpService.BrowseSource(conn);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                SELECT type, rating, popularity_type_current FROM {source}
                WHERE state = 'active' AND type IS NOT NULL AND type != 'novel'
                  AND rating IS NOT NULL AND popularity_type_current IS NOT NULL
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var type = reader.GetString(0);
                if (!byType.TryGetValue(type, out var rows))
                {
                    byType[type] = rows = [];
                }

                rows.Add((reader.GetInt64(2), reader.GetDouble(1)));
            }
        }

        var groups = new Dictionary<string, double[]>(StringComparer.Ordinal);
        var all = new List<double>();
        foreach (var (type, rows) in byType)
        {
            var keep = (int)Math.Ceiling(rows.Count * PopularShare);
            var popular = rows.OrderBy(r => r.Rank).Take(keep).Select(r => r.Rating).ToArray();
            all.AddRange(popular);
            if (popular.Length >= minGroupSize)
            {
                Array.Sort(popular);
                groups[type] = popular;
            }
        }

        var allSorted = all.ToArray();
        Array.Sort(allSorted);
        groups[AllGroup] = allSorted;

        var histograms = groups.ToDictionary(g => g.Key, g => Histogram(g.Value), StringComparer.Ordinal);
        return new Snapshot(groups, histograms, info.LastWriteTimeUtc.Ticks, info.Length);
    }

    private static int[] Histogram(double[] ratings)
    {
        var bins = new int[Bins];
        foreach (var rating in ratings)
        {
            if (rating < 50)
            {
                continue;
            }

            bins[Math.Min(Bins - 1, (int)((rating - 50) / 5))]++;
        }

        return bins;
    }

    private static int LowerBound(double[] sorted, double value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (sorted[mid] < value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
