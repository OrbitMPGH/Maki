using Maki.Metadata.MangaBaka;

namespace Maki.Metadata.Embedding;

/// <summary>
/// A <see cref="RecommendationFilters"/> set resolved against this index's vocabularies once per
/// search, so the per-row test is integer comparisons instead of string work.
/// <see cref="Impossible"/> means a requested name isn't in the vocabulary at all — no row can
/// match, same as the SQL clause's <c>IN ()</c> would give.
/// </summary>
public sealed record FilterPlan(
    int? YearMin,
    int? YearMax,
    double? MinRating,
    int? MinChapters,
    int? MaxChapters,
    byte[]? Types,
    byte[]? Statuses,
    int[]? Genres,
    bool Impossible)
{
    public static readonly FilterPlan None = new(null, null, null, null, null, null, null, null, false);

    public bool IsEmpty =>
        !Impossible && YearMin is null && YearMax is null && MinRating is null &&
        MinChapters is null && MaxChapters is null && Types is null && Statuses is null && Genres is null;
}

/// <summary>
/// The whole embedding index, in memory, laid out for a linear scan: every candidate's vector
/// int8-quantized into one flat array (<see cref="EmbeddingMath.Quantize"/>) plus the handful of
/// dump columns the filters need. Natural-language search cosines the query against every row, so
/// this has to be RAM-resident — reading ~200 MB of BLOBs out of SQLite per keystroke is what the
/// recommender does, and it takes seconds.
///
/// Filter semantics deliberately mirror <see cref="RecommendationFilters.BuildClause"/> (unknown
/// year/chapter counts fall out of a bounded range; genre/type/status matching is
/// case-insensitive and every selected value must be present). The two are tested against each
/// other's behaviour rather than sharing code, since one is SQL and one is a row test.
///
/// Tags are the one filter not handled here — they live in a separate table as packed blobs and
/// are applied to the (small) candidate pool by the caller.
/// </summary>
public sealed class VectorIndex(
    long[] ids,
    sbyte[] data,
    float[] scales,
    int dimensions,
    int[] years,
    float[] ratings,
    int[] chapters,
    byte[] types,
    byte[] statuses,
    int[][] genres,
    IReadOnlyDictionary<string, byte> typeIds,
    IReadOnlyDictionary<string, byte> statusIds,
    IReadOnlyDictionary<string, int> genreIds)
{
    /// <summary>Sentinel for a column the dump left null (or unparseable), used by years/chapters.</summary>
    public const int Unknown = -1;

    private readonly Dictionary<long, int> _rowById = BuildRowMap(ids);

    public int Count => ids.Length;

    public int Dimensions => dimensions;

    public long IdAt(int row) => ids[row];

    public double RatingAt(int row) => ratings[row];

    public bool TryGetRow(long id, out int row) => _rowById.TryGetValue(id, out row);

    /// <summary>Resolves filter names to this index's ids. Cheap; call once per search.</summary>
    public FilterPlan Plan(RecommendationFilters? filters)
    {
        if (filters is null || ReferenceEquals(filters, RecommendationFilters.None))
        {
            return FilterPlan.None;
        }

        var impossible = false;

        byte[]? ResolveBytes(IReadOnlyList<string>? names, IReadOnlyDictionary<string, byte> vocab)
        {
            if (names is not { Count: > 0 })
            {
                return null;
            }

            var resolved = names.Where(vocab.ContainsKey).Select(n => vocab[n]).Distinct().ToArray();
            // An IN-list of names none of which exist can still match nothing, but one that
            // resolves partially is fine — IN is a disjunction.
            impossible |= resolved.Length == 0;
            return resolved;
        }

        int[]? resolvedGenres = null;
        if (filters.Genres is { Count: > 0 } wanted)
        {
            resolvedGenres = new int[wanted.Count];
            for (var i = 0; i < wanted.Count; i++)
            {
                // Genres are ANDed, so a single unknown name means nothing can match.
                if (!genreIds.TryGetValue(wanted[i], out var id))
                {
                    impossible = true;
                    break;
                }

                resolvedGenres[i] = id;
            }
        }

        return new FilterPlan(
            filters.YearMin,
            filters.YearMax,
            filters.MinRating,
            filters.MinChapters,
            filters.MaxChapters,
            ResolveBytes(filters.Types, typeIds),
            ResolveBytes(filters.Statuses, statusIds),
            resolvedGenres,
            impossible);
    }

    public bool Matches(int row, FilterPlan plan)
    {
        if (plan.Impossible)
        {
            return false;
        }

        if (plan.YearMin is int ymin && (years[row] == Unknown || years[row] < ymin))
        {
            return false;
        }

        if (plan.YearMax is int ymax && (years[row] == Unknown || years[row] > ymax))
        {
            return false;
        }

        if (plan.MinRating is double mr && ratings[row] < mr)
        {
            return false;
        }

        if (plan.MinChapters is int cmin && (chapters[row] == Unknown || chapters[row] < cmin))
        {
            return false;
        }

        if (plan.MaxChapters is int cmax && (chapters[row] == Unknown || chapters[row] > cmax))
        {
            return false;
        }

        if (plan.Types is { } wantTypes && Array.IndexOf(wantTypes, types[row]) < 0)
        {
            return false;
        }

        if (plan.Statuses is { } wantStatuses && Array.IndexOf(wantStatuses, statuses[row]) < 0)
        {
            return false;
        }

        if (plan.Genres is { } wantGenres)
        {
            var rowGenres = genres[row];
            foreach (var g in wantGenres)
            {
                if (Array.IndexOf(rowGenres, g) < 0)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// The <paramref name="take"/> rows whose vectors are closest to <paramref name="query"/>
    /// (which must be unit-normalized), highest cosine first, skipping rows the plan rejects.
    /// </summary>
    public IReadOnlyList<(int Row, float Cosine)> Search(
        float[] query, FilterPlan plan, int take, CancellationToken ct = default)
    {
        if (Count == 0 || take <= 0 || query.Length != dimensions || plan.Impossible)
        {
            return [];
        }

        var scores = new float[Count];
        Parallel.For(
            0,
            Count,
            new ParallelOptions { CancellationToken = ct },
            () => new float[dimensions],
            (row, _, buffer) =>
            {
                scores[row] = Matches(row, plan)
                    ? EmbeddingMath.QuantizedDot(
                        query, data.AsSpan(row * dimensions, dimensions), scales[row], buffer)
                    : float.NegativeInfinity;
                return buffer;
            },
            _ => { });

        // Collect the survivors and sort them rather than heap-selecting: at index sizes in the
        // low hundreds of thousands the sort is a few milliseconds and the code stays obvious.
        var rows = new List<int>(Math.Min(Count, 4096));
        for (var row = 0; row < Count; row++)
        {
            if (!float.IsNegativeInfinity(scores[row]))
            {
                rows.Add(row);
            }
        }

        var keys = new float[rows.Count];
        var values = rows.ToArray();
        for (var i = 0; i < values.Length; i++)
        {
            keys[i] = -scores[values[i]]; // ascending sort on the negation = descending by cosine
        }

        Array.Sort(keys, values);
        return values.Take(take).Select(row => (row, scores[row])).ToList();
    }

    private static Dictionary<long, int> BuildRowMap(long[] ids)
    {
        var map = new Dictionary<long, int>(ids.Length);
        for (var i = 0; i < ids.Length; i++)
        {
            map[ids[i]] = i;
        }

        return map;
    }
}
