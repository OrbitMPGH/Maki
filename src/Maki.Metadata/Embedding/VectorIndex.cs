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
    int[][]? Tags,
    byte[]? ContentRatings,
    bool Impossible,
    bool[]? CreditMask = null)
{
    public static readonly FilterPlan None = new(null, null, null, null, null, null, null, null, null, null, false);

    public bool IsEmpty =>
        !Impossible && YearMin is null && YearMax is null && MinRating is null &&
        MinChapters is null && MaxChapters is null && Types is null && Statuses is null &&
        Genres is null && Tags is null && ContentRatings is null && CreditMask is null;
}

/// <summary>
/// The per-row dump columns the filters and the hybrid scorer need, each array parallel to the
/// index's vector rows. Grouped into a record so the index's constructor stays readable as columns
/// are added; nothing here is meaningful on its own.
/// </summary>
/// <param name="Authors">
/// Interned author ids. Present so the recommender's author-match term can be answered from RAM;
/// it is a set-intersection test, so the names themselves are never needed at scan time.
/// </param>
/// <param name="Franchise">
/// Which same-work component the row belongs to (<see cref="MangaBaka.FranchiseGraph"/>), or
/// <see cref="VectorIndex.Unknown"/> for the common case of a series in no franchise. Never confuse
/// the two: component 0 is a real franchise.
/// </param>
/// <param name="Popularity">
/// <c>popularity_global_current</c> — a global rank where 1 is the most popular, or
/// <see cref="VectorIndex.Unknown"/>. Feeds the obscurity term.
/// </param>
public sealed record VectorIndexColumns(
    int[] Years,
    float[] Ratings,
    int[] Chapters,
    byte[] Types,
    byte[] Statuses,
    int[][] Genres,
    int[][] Authors,
    int[] Popularity,
    byte[]?[] TagBlobs,
    byte[] ContentRatings,
    int[] Franchise);

/// <summary>
/// The interned vocabularies behind <see cref="VectorIndexColumns"/>, so a per-row filter test is
/// integer comparisons rather than string work. All are case-insensitive, matching the SQL clause.
/// </summary>
/// <param name="Tags">
/// Tag name → every vocabulary id carrying that name. One name can have several ids because
/// casing variants are interned separately ("Childhood love" and "Childhood Love"), and carrying
/// any one of them satisfies the name.
/// </param>
public sealed record VectorIndexVocabularies(
    IReadOnlyDictionary<string, byte> Types,
    IReadOnlyDictionary<string, byte> Statuses,
    IReadOnlyDictionary<string, int> Genres,
    IReadOnlyDictionary<string, int> Authors,
    IReadOnlyDictionary<string, int[]> Tags,
    IReadOnlyDictionary<string, byte> ContentRatings);

/// <summary>
/// The behavioural vectors, quantized and row-aligned to a <see cref="VectorIndex"/>. Its own type
/// rather than more columns on <see cref="VectorIndexColumns"/> because it arrives from a separate,
/// independently installed artifact and is usually absent: a row with no vector carries scale 0,
/// which every reader treats as "no behavioural evidence" and never as a similarity of zero.
///
/// <para>
/// Its dimensionality is deliberately NOT the text index's. The two spaces answer different
/// questions and are trained by different things, so they are quantized separately and never
/// concatenated.
/// </para>
/// </summary>
/// <param name="Covered">
/// How many rows actually carry a vector. Only useful for logging, but the number is the whole
/// argument for the channel existing, so it is worth being able to print.
/// </param>
public sealed record TasteLayer(sbyte[] Data, float[] Scales, int Dimensions, int Covered);

/// <summary>
/// The whole embedding index, in memory, laid out for a linear scan: every candidate's vector
/// int8-quantized into one flat array (<see cref="EmbeddingMath.Quantize"/>) plus the handful of
/// dump columns the filters and the hybrid scorer need. Both natural-language search and the
/// library recommender cosine a query against every row, so this has to be RAM-resident — reading
/// the same BLOBs back out of SQLite per query takes seconds.
///
/// Filter semantics deliberately mirror <see cref="RecommendationFilters.BuildClause"/> (unknown
/// year/chapter counts fall out of a bounded range; genre/type/status matching is
/// case-insensitive and every selected value must be present). The two are tested against each
/// other's behaviour rather than sharing code, since one is SQL and one is a row test.
///
/// Tags are handled here too, off the packed blobs the index already carries, and it matters that
/// they are: applied to the result page instead, a tag filter can only ever *remove* rows the
/// other channels happened to rank, so asking for a tag would narrow the page rather than search
/// within that tag. Every filter has to be a per-row test before top-K or the page silently
/// truncates to whatever survived.
/// </summary>
public sealed class VectorIndex(
    long[] ids,
    sbyte[] data,
    float[] scales,
    int dimensions,
    VectorIndexColumns columns,
    VectorIndexVocabularies vocabularies,
    TasteLayer? taste = null)
{
    /// <summary>Sentinel for a column the dump left null (or unparseable), used by years/chapters/popularity.</summary>
    public const int Unknown = -1;

    private readonly Dictionary<long, int> _rowById = BuildRowMap(ids);

    public int Count => ids.Length;

    public int Dimensions => dimensions;

    public long IdAt(int row) => ids[row];

    public double RatingAt(int row) => columns.Ratings[row];

    /// <summary>The row's popularity rank (1 = most popular), or <see cref="Unknown"/>.</summary>
    public int PopularityAt(int row) => columns.Popularity[row];

    /// <summary>The row's release year, or <see cref="Unknown"/>. Feeds the browse orderings.</summary>
    public int YearAt(int row) => columns.Years[row];

    /// <summary>The row's interned genre ids — resolve names through <see cref="TryGetGenreId"/>.</summary>
    public int[] GenresAt(int row) => columns.Genres[row];

    /// <summary>The row's interned author ids — resolve names through <see cref="TryGetAuthorId"/>.</summary>
    public int[] AuthorsAt(int row) => columns.Authors[row];

    /// <summary>The row's packed tags (<see cref="TagMath"/>), or null when it has none.</summary>
    public byte[]? TagsAt(int row) => columns.TagBlobs[row];

    /// <summary>
    /// The row's same-work component, or <see cref="Unknown"/> when it is in no franchise. Shared by
    /// the ranker's collapse and the eval's franchise metric, so the number that measures the
    /// problem cannot drift from the code that fixes it.
    /// </summary>
    public int FranchiseAt(int row) => columns.Franchise[row];

    public bool TryGetRow(long id, out int row) => _rowById.TryGetValue(id, out row);

    public bool TryGetGenreId(string name, out int id) => vocabularies.Genres.TryGetValue(name, out id);

    public bool TryGetAuthorId(string name, out int id) => vocabularies.Authors.TryGetValue(name, out id);

    /// <summary>
    /// Cosine of one row against a query packed by <see cref="EmbeddingMath.QuantizeQuery"/>.
    /// Exposed so a caller that scores rows itself (the recommender's hybrid pass) can reuse the
    /// index's vectors without a second copy of the quantization details.
    /// </summary>
    public float CosineAt(int row, ReadOnlySpan<sbyte> query, float queryScale) =>
        EmbeddingMath.QuantizedDot(query, queryScale, Row(row), scales[row]);

    /// <summary>
    /// Cosine between two indexed rows, straight off the packed bytes. This is the similarity MMR
    /// diversifies on; doing it here keeps the candidates quantized instead of materializing a
    /// float vector per pool entry.
    /// </summary>
    public float CosineBetween(int rowA, int rowB) =>
        EmbeddingMath.QuantizedDot(Row(rowA), scales[rowA], Row(rowB), scales[rowB]);

    private ReadOnlySpan<sbyte> Row(int row) => data.AsSpan(row * dimensions, dimensions);

    /// <summary>
    /// The behavioural vectors, row-aligned to this index, or null when no artifact is installed.
    /// Absent is the normal state and must cost a candidate nothing.
    /// </summary>
    public TasteLayer? Taste => taste;

    /// <summary>True when this row has a behavioural vector at all.</summary>
    public bool HasTasteAt(int row) => taste is not null && taste.Scales[row] != 0;

    /// <summary>
    /// Cosine of one row's BEHAVIOURAL vector against a taste query, or 0 when either side has no
    /// vector. Zero means "no evidence", the same contract <c>RecoGraphScorer</c> has, and the
    /// scorer must not be able to tell it apart from a genuine zero similarity.
    /// </summary>
    public float TasteCosineAt(int row, ReadOnlySpan<sbyte> query, float queryScale)
    {
        if (taste is null || taste.Scales[row] == 0)
        {
            return 0;
        }

        return EmbeddingMath.QuantizedDot(
            query, queryScale, taste.Data.AsSpan(row * taste.Dimensions, taste.Dimensions), taste.Scales[row]);
    }

    /// <summary>The row's behavioural vector as floats, for building a seed centroid. Null if absent.</summary>
    public float[]? TasteVectorAt(int row)
    {
        if (taste is null || taste.Scales[row] == 0)
        {
            return null;
        }

        var vec = new float[taste.Dimensions];
        var offset = row * taste.Dimensions;
        for (var d = 0; d < taste.Dimensions; d++)
        {
            vec[d] = taste.Data[offset + d] * taste.Scales[row];
        }

        return vec;
    }

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
                if (!vocabularies.Genres.TryGetValue(wanted[i], out var id))
                {
                    impossible = true;
                    break;
                }

                resolvedGenres[i] = id;
            }
        }

        int[][]? resolvedTags = null;
        if (filters.Tags is { Count: > 0 } wantedTags)
        {
            resolvedTags = new int[wantedTags.Count][];
            for (var i = 0; i < wantedTags.Count; i++)
            {
                // Tags are ANDed like genres, so an unknown name means nothing can match. Each
                // name resolves to the set of ids sharing it; carrying any one of them satisfies it.
                if (!vocabularies.Tags.TryGetValue(wantedTags[i], out var ids) || ids.Length == 0)
                {
                    impossible = true;
                    break;
                }

                resolvedTags[i] = ids;
            }
        }

        return new FilterPlan(
            filters.YearMin,
            filters.YearMax,
            filters.MinRating,
            filters.MinChapters,
            filters.MaxChapters,
            ResolveBytes(filters.Types, vocabularies.Types),
            ResolveBytes(filters.Statuses, vocabularies.Statuses),
            resolvedGenres,
            resolvedTags,
            ResolveBytes(filters.ContentRatings, vocabularies.ContentRatings),
            impossible);
    }

    /// <summary>
    /// Builds a per-row allow mask from a set of MangaBaka ids, for
    /// <see cref="FilterPlan.CreditMask"/>. Ids this index does not carry are simply absent from
    /// the mask, which is the right answer: an unrated or novel series is not searchable here
    /// whether or not its author matched.
    /// </summary>
    public bool[] BuildRowMask(ReadOnlySpan<long> ids)
    {
        var mask = new bool[Count];
        foreach (var id in ids)
        {
            if (_rowById.TryGetValue(id, out var row))
            {
                mask[row] = true;
            }
        }

        return mask;
    }

    public bool Matches(int row, FilterPlan plan)
    {
        if (plan.Impossible)
        {
            return false;
        }

        // First, and an array index rather than a set probe: this runs inside the parallel scan
        // over every row, twice per search, so it is the one filter test worth making branch-cheap.
        if (plan.CreditMask is { } credits && !credits[row])
        {
            return false;
        }

        if (plan.YearMin is int ymin && (columns.Years[row] == Unknown || columns.Years[row] < ymin))
        {
            return false;
        }

        if (plan.YearMax is int ymax && (columns.Years[row] == Unknown || columns.Years[row] > ymax))
        {
            return false;
        }

        if (plan.MinRating is double mr && columns.Ratings[row] < mr)
        {
            return false;
        }

        if (plan.MinChapters is int cmin && (columns.Chapters[row] == Unknown || columns.Chapters[row] < cmin))
        {
            return false;
        }

        if (plan.MaxChapters is int cmax && (columns.Chapters[row] == Unknown || columns.Chapters[row] > cmax))
        {
            return false;
        }

        if (plan.Types is { } wantTypes && Array.IndexOf(wantTypes, columns.Types[row]) < 0)
        {
            return false;
        }

        if (plan.Statuses is { } wantStatuses && Array.IndexOf(wantStatuses, columns.Statuses[row]) < 0)
        {
            return false;
        }

        if (plan.Genres is { } wantGenres)
        {
            var rowGenres = columns.Genres[row];
            foreach (var g in wantGenres)
            {
                if (Array.IndexOf(rowGenres, g) < 0)
                {
                    return false;
                }
            }
        }

        if (plan.Tags is { } wantTags && !TagMath.ContainsAll(columns.TagBlobs[row], wantTags))
        {
            return false;
        }

        if (plan.ContentRatings is { } wantRatings && Array.IndexOf(wantRatings, columns.ContentRatings[row]) < 0)
        {
            return false;
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

        var packedQuery = EmbeddingMath.QuantizeQuery(query, out var queryScale);
        var scores = new float[Count];
        Parallel.For(
            0,
            Count,
            new ParallelOptions { CancellationToken = ct },
            row => scores[row] = Matches(row, plan)
                ? CosineAt(row, packedQuery, queryScale)
                : float.NegativeInfinity);

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
