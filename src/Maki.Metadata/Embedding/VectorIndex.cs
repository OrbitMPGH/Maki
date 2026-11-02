using System.Buffers;
using Maki.Core.Recommendations;
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
    bool[]? CreditMask = null,
    PlannedRule[]? Rules = null,
    PlannedRule? Hidden = null,
    bool[]? Exclude = null,
    int? MinStartDay = null)
{
    public static readonly FilterPlan None = new(null, null, null, null, null, null, null, null, null, null, false);

    public bool IsEmpty =>
        !Impossible && YearMin is null && YearMax is null && MinRating is null &&
        MinChapters is null && MaxChapters is null && Types is null && Statuses is null &&
        Genres is null && Tags is null && ContentRatings is null && CreditMask is null &&
        Rules is null && Hidden is null && Exclude is null && MinStartDay is null;

    /// <summary>Narrows <see cref="CreditMask"/> to rows <paramref name="mask"/> also allows.</summary>
    public FilterPlan RestrictTo(bool[] mask)
    {
        if (CreditMask is not { } current)
        {
            return this with { CreditMask = mask };
        }

        var both = new bool[mask.Length];
        for (var i = 0; i < both.Length; i++)
        {
            both[i] = mask[i] && current[i];
        }

        return this with { CreditMask = both };
    }
}

/// <summary>A <see cref="CatalogueRule"/> resolved to this index's ids.</summary>
public sealed record PlannedRule(string Mode, PlannedTerm[] Terms);

/// <summary>
/// One resolved term: a genre id, or a sorted set of tag ids (the name's casing variants, plus its
/// subtree when asked) that count only at <paramref name="MinClass"/> or above.
/// </summary>
public readonly record struct PlannedTerm(int GenreId, int[]? TagIds, byte MinClass);

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
/// the two: component 0 is a real franchise. May be empty when the index has a deferred loader;
/// consumers read through <see cref="VectorIndex.FranchiseAt"/> in either case.
/// </param>
/// <param name="Artists">
/// Interned artist ids, sharing <paramref name="Authors"/>' vocabulary so one person matches across
/// both roles. Its own column rather than pre-unioned, because whether the credit channel counts
/// artists is a tuning question and the index is shared across every variant of a sweep.
/// </param>
/// <param name="Popularity">
/// <c>popularity_global_current</c> — a global rank where 1 is the most popular, or
/// <see cref="VectorIndex.Unknown"/>. Feeds the obscurity term.
/// </param>
/// <param name="StartDays">
/// <c>published_start_date</c> as a <see cref="DateOnly.DayNumber"/>, or <see cref="VectorIndex.Unknown"/>.
/// Optional so fixtures built without it still sort, through <see cref="VectorIndex.StartDayAt"/>'s
/// year fallback.
/// </param>
public sealed record VectorIndexColumns(
    int[] Years,
    float[] Ratings,
    int[] Chapters,
    byte[] Types,
    byte[] Statuses,
    JaggedInts Genres,
    JaggedInts Authors,
    JaggedInts Artists,
    int[] Popularity,
    PackedTags Tags,
    byte[] ContentRatings,
    int[] Franchise,
    int[]? StartDays = null);

/// <summary>
/// The interned vocabularies behind <see cref="VectorIndexColumns"/>, so a per-row filter test is
/// integer comparisons rather than string work. Lookups are case-insensitive; the SQL clause compares
/// type and status exactly, which agrees for the lowercase values the dump holds.
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
    IReadOnlyDictionary<string, byte> ContentRatings,
    IReadOnlyDictionary<string, int[]>? TagSubtrees = null);

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
///
/// <para>
/// Stores only the rows that have a vector (about half the index), with a slot per row pointing at
/// them; a full row-aligned array spent 8 MB on rows the artifact does not cover.
/// </para>
/// </summary>
public sealed class TasteLayer
{
    private readonly int[] _slots;
    private readonly sbyte[] _data;
    private readonly float[] _scales;

    /// <param name="slots">Per index row, the slot holding its vector, or -1 for none.</param>
    /// <param name="data">The covered vectors, one per slot, <paramref name="dimensions"/> wide.</param>
    /// <param name="scales">One non-zero scale per slot.</param>
    public TasteLayer(int[] slots, sbyte[] data, float[] scales, int dimensions)
    {
        _slots = slots;
        _data = data;
        _scales = scales;
        Dimensions = dimensions;
    }

    public int Dimensions { get; }

    /// <summary>
    /// How many rows actually carry a vector. Only useful for logging, but the number is the whole
    /// argument for the channel existing, so it is worth being able to print.
    /// </summary>
    public int Covered => _scales.Length;

    public bool Has(int row) => _slots[row] >= 0;

    /// <summary>The row's vector. Check <see cref="Has"/> first.</summary>
    public ReadOnlySpan<sbyte> VectorAt(int row) => _data.AsSpan(_slots[row] * Dimensions, Dimensions);

    /// <summary>The row's scale. Check <see cref="Has"/> first.</summary>
    public float ScaleAt(int row) => _scales[_slots[row]];

    /// <summary>Builds a layer from row-aligned vectors, where a scale of 0 marks a row with none.</summary>
    public static TasteLayer FromRows(sbyte[] data, float[] scales, int dimensions)
    {
        var slots = new int[scales.Length];
        var covered = 0;
        for (var row = 0; row < scales.Length; row++)
        {
            slots[row] = scales[row] == 0 ? -1 : covered++;
        }

        var packed = new sbyte[covered * dimensions];
        var packedScales = new float[covered];
        for (var row = 0; row < scales.Length; row++)
        {
            if (slots[row] >= 0)
            {
                data.AsSpan(row * dimensions, dimensions).CopyTo(packed.AsSpan(slots[row] * dimensions));
                packedScales[slots[row]] = scales[row];
            }
        }

        return new TasteLayer(slots, packed, packedScales, dimensions);
    }
}

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
/// <param name="data">
/// Every row's vector, 4-bit levels packed two to a byte (<see cref="EmbeddingMath.PackQuantized"/>),
/// laid out row-major at <see cref="EmbeddingMath.PackedStride"/> bytes each. Build one with
/// <see cref="FromQuantized"/> rather than by hand; <paramref name="scales"/> must already carry the
/// packing step, which is what that does.
/// </param>
public sealed class VectorIndex(
    long[] ids,
    byte[] data,
    float[] scales,
    int dimensions,
    VectorIndexColumns columns,
    VectorIndexVocabularies vocabularies,
    TasteLayer? taste = null,
    Func<int[]>? franchiseLoader = null)
{
    /// <summary>Sentinel for a column the dump left null (or unparseable), used by years/chapters/popularity.</summary>
    public const int Unknown = -1;

    /// <summary>
    /// Builds an index from one int8 vector per row, packing as it goes and folding the packing
    /// step into each row's scale.
    ///
    /// <para>
    /// For callers that already hold the int8 form. <see cref="VectorIndexCache"/> deliberately does
    /// not use this: it packs each row as it reads it out of SQLite, so the int8 array — 93 MB at
    /// catalogue scale — never exists at all.
    /// </para>
    /// </summary>
    public static VectorIndex FromQuantized(
        long[] ids,
        sbyte[] data,
        float[] scales,
        int dimensions,
        VectorIndexColumns columns,
        VectorIndexVocabularies vocabularies,
        TasteLayer? taste = null,
        Func<int[]>? franchiseLoader = null)
    {
        var stride = EmbeddingMath.PackedStride(dimensions);
        var packed = new byte[(long)ids.Length * stride];
        var packedScales = new float[scales.Length];
        for (var row = 0; row < ids.Length; row++)
        {
            var step = EmbeddingMath.PackQuantized(
                data.AsSpan(row * dimensions, dimensions),
                packed.AsSpan(row * stride, stride));
            packedScales[row] = scales[row] * step;
        }

        return new VectorIndex(
            ids, packed, packedScales, dimensions, columns, vocabularies, taste, franchiseLoader);
    }

    // Null when the ids are sorted, which is how the cache builds them: a binary search over the id
    // array then replaces a 128k-entry dictionary.
    private readonly Dictionary<long, int>? _rowById = IsSorted(ids) ? null : BuildRowMap(ids);
    private readonly Lazy<int[]> _franchises = new(() => franchiseLoader?.Invoke() ?? columns.Franchise);

    public int Count => ids.Length;

    public int Dimensions => dimensions;

    public long IdAt(int row) => ids[row];

    public double RatingAt(int row) => columns.Ratings[row];

    /// <summary>The row's popularity rank (1 = most popular), or <see cref="Unknown"/>.</summary>
    public int PopularityAt(int row) => columns.Popularity[row];

    /// <summary>The row's release year, or <see cref="Unknown"/>. Feeds the browse orderings.</summary>
    public int YearAt(int row) => columns.Years[row];

    /// <summary>
    /// The row's first publication date as a <see cref="DateOnly.DayNumber"/>, falling back to
    /// January 1st of <see cref="YearAt"/> when the dump has no date, or <see cref="Unknown"/>.
    /// May be in the future: the dump lists announced titles.
    /// </summary>
    public int StartDayAt(int row)
    {
        if (columns.StartDays is { } days && days[row] != Unknown)
        {
            return days[row];
        }

        var year = columns.Years[row];
        return year is >= 1 and <= 9999 ? new DateOnly(year, 1, 1).DayNumber : Unknown;
    }

    /// <summary>The row's interned genre ids — resolve names through <see cref="TryGetGenreId"/>.</summary>
    public ReadOnlySpan<int> GenresAt(int row) => columns.Genres[row];

    /// <summary>The row's interned author ids — resolve names through <see cref="TryGetAuthorId"/>.</summary>
    public ReadOnlySpan<int> AuthorsAt(int row) => columns.Authors[row];

    /// <summary>The row's interned artist ids, from the same vocabulary as its authors.</summary>
    public ReadOnlySpan<int> ArtistsAt(int row) => columns.Artists[row];

    /// <summary>The row's packed tags (<see cref="TagMath"/>), empty when it has none.</summary>
    public ReadOnlySpan<byte> TagsAt(int row) => columns.Tags[row];

    /// <summary>
    /// The row's same-work component, or <see cref="Unknown"/> when it is in no franchise. Shared by
    /// the ranker's collapse and the eval's franchise metric, so the number that measures the
    /// problem cannot drift from the code that fixes it. A deferred graph is built once on first
    /// access, including when multiple callers arrive together. Ordinary search never needs it, and
    /// neither does the ranker on its default settings, since both franchise-suppression knobs ship
    /// disabled; what does reach it is <c>RecommendationService</c>, which reads the component per
    /// pick to space a franchise out on the surfaces rather than to drop anything.
    /// </summary>
    public int FranchiseAt(int row) => _franchises.Value[row];

    public bool TryGetRow(long id, out int row)
    {
        if (_rowById is not null)
        {
            return _rowById.TryGetValue(id, out row);
        }

        row = Array.BinarySearch(ids, id);
        return row >= 0;
    }

    public bool TryGetGenreId(string name, out int id) => vocabularies.Genres.TryGetValue(name, out id);

    public bool TryGetAuthorId(string name, out int id) => vocabularies.Authors.TryGetValue(name, out id);

    /// <summary>
    /// Every vocabulary id carrying this tag name. Several, because casing variants are interned
    /// separately; a row carrying any one of them carries the name.
    /// </summary>
    public bool TryGetTagIds(string name, out IReadOnlyList<int> ids)
    {
        var found = vocabularies.Tags.TryGetValue(name, out var tagIds);
        ids = tagIds ?? [];
        return found;
    }

    /// <summary>Every interned genre name with its id.</summary>
    public IReadOnlyDictionary<string, int> GenreVocabulary => vocabularies.Genres;

    /// <summary>Every interned tag name with the ids of its casing variants.</summary>
    public IReadOnlyDictionary<string, int[]> TagVocabulary => vocabularies.Tags;

    /// <summary>
    /// Cosine of one row against a query packed by <see cref="EmbeddingMath.QuantizeQuery"/>.
    /// Exposed so a caller that scores rows itself (the recommender's hybrid pass) can reuse the
    /// index's vectors without a second copy of the quantization details.
    /// </summary>
    public float CosineAt(int row, ReadOnlySpan<sbyte> query, float queryScale)
    {
        Span<sbyte> unpacked = stackalloc sbyte[dimensions];
        UnpackRow(row, unpacked);
        return EmbeddingMath.QuantizedDot(query, queryScale, unpacked, scales[row]);
    }

    /// <summary>
    /// This row's levels, expanded. For a caller dotting ONE row against several queries — which is
    /// what a multi-seed scan does — unpacking once here and calling
    /// <see cref="EmbeddingMath.QuantizedDot"/> per query is the difference between one expansion
    /// per row and one per row per query: at 48 seed queries over a 126k-row index, 126 thousand
    /// against six million.
    /// </summary>
    public void UnpackRow(int row, Span<sbyte> dest) =>
        EmbeddingMath.UnpackQuantized(PackedRow(row), dest);

    /// <summary>This row's quantization scale, for a caller that unpacked the row itself.</summary>
    public float ScaleAt(int row) => scales[row];

    /// <summary>
    /// Cosine between two indexed rows, straight off the packed bytes. This is the similarity MMR
    /// diversifies on; doing it here keeps the candidates quantized instead of materializing a
    /// float vector per pool entry.
    /// </summary>
    public float CosineBetween(int rowA, int rowB)
    {
        Span<sbyte> a = stackalloc sbyte[dimensions];
        Span<sbyte> b = stackalloc sbyte[dimensions];
        UnpackRow(rowA, a);
        UnpackRow(rowB, b);
        return EmbeddingMath.QuantizedDot(a, scales[rowA], b, scales[rowB]);
    }

    private ReadOnlySpan<byte> PackedRow(int row) =>
        data.AsSpan(row * EmbeddingMath.PackedStride(dimensions), EmbeddingMath.PackedStride(dimensions));

    /// <summary>
    /// The behavioural vectors, row-aligned to this index, or null when no artifact is installed.
    /// Absent is the normal state and must cost a candidate nothing.
    /// </summary>
    public TasteLayer? Taste => taste;

    /// <summary>True when this row has a behavioural vector at all.</summary>
    public bool HasTasteAt(int row) => taste is not null && taste.Has(row);

    /// <summary>
    /// Cosine of one row's BEHAVIOURAL vector against a taste query, or 0 when either side has no
    /// vector. Zero means "no evidence", the same contract <c>RecoGraphScorer</c> has, and the
    /// scorer must not be able to tell it apart from a genuine zero similarity.
    /// </summary>
    public float TasteCosineAt(int row, ReadOnlySpan<sbyte> query, float queryScale)
    {
        if (taste is null || !taste.Has(row))
        {
            return 0;
        }

        return EmbeddingMath.QuantizedDot(query, queryScale, taste.VectorAt(row), taste.ScaleAt(row));
    }

    /// <summary>
    /// The row's TEXT vector as floats, dequantized. The counterpart to
    /// <see cref="TasteVectorAt"/> for the other space, and here for the same reason: a caller
    /// building a centroid to hand back to <see cref="Search"/> needs the vectors themselves, not
    /// just cosines against them.
    /// <para>
    /// Always present — every indexed row has a text vector, which is what put it in the index.
    /// </para>
    /// </summary>
    public float[] VectorAt(int row)
    {
        Span<sbyte> unpacked = stackalloc sbyte[dimensions];
        UnpackRow(row, unpacked);
        var vec = new float[dimensions];
        for (var d = 0; d < dimensions; d++)
        {
            vec[d] = unpacked[d] * scales[row];
        }

        return vec;
    }

    /// <summary>
    /// Cosine between two rows' BEHAVIOURAL vectors, or 0 when either lacks one. Zero is "no
    /// evidence", never a genuine dissimilarity — the same contract <see cref="TasteCosineAt"/>
    /// has, and callers must treat an absent channel as absent rather than as disagreement.
    /// </summary>
    public float TasteCosineBetween(int rowA, int rowB)
    {
        if (taste is null || !taste.Has(rowA) || !taste.Has(rowB))
        {
            return 0;
        }

        return EmbeddingMath.QuantizedDot(
            taste.VectorAt(rowA), taste.ScaleAt(rowA), taste.VectorAt(rowB), taste.ScaleAt(rowB));
    }

    /// <summary>The row's behavioural vector as floats, for building a seed centroid. Null if absent.</summary>
    public float[]? TasteVectorAt(int row)
    {
        if (taste is null || !taste.Has(row))
        {
            return null;
        }

        var vec = new float[taste.Dimensions];
        var levels = taste.VectorAt(row);
        var scale = taste.ScaleAt(row);
        for (var d = 0; d < taste.Dimensions; d++)
        {
            vec[d] = levels[d] * scale;
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

        byte[]? ResolveBytes(
            IReadOnlyList<string>? names, IReadOnlyDictionary<string, byte> vocab, bool emptyMatchesNothing = false)
        {
            if (names is null || (names.Count == 0 && !emptyMatchesNothing))
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

        List<PlannedRule>? rules = null;
        foreach (var rule in filters.Rules ?? [])
        {
            if (PlanRule(rule.Mode, rule.Terms, ref impossible) is { } planned)
            {
                (rules ??= []).Add(planned);
            }
        }

        var hidden = filters.Hidden is { Count: > 0 } hiddenTerms
            ? PlanRule(CatalogueRules.None, hiddenTerms, ref impossible)
            : null;

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
            // A ceiling-resolved rating list is never empty, so an empty one here means something
            // upstream dropped every rating; matching nothing is the only safe reading of that.
            ResolveBytes(
                ContentRating.CoversAll(filters.ContentRatings) ? null : filters.ContentRatings,
                vocabularies.ContentRatings, emptyMatchesNothing: true),
            impossible || filters.CreditIds is { Count: 0 },
            CreditMask: filters.CreditIds is { Count: > 0 } creditIds ? BuildRowMask(creditIds.ToArray()) : null,
            Rules: rules?.ToArray(),
            Hidden: hidden);
    }

    /// <summary>
    /// Resolves one rule. A name the index does not know cannot be carried by any row, so it sinks
    /// an "all" rule, drops out of "any" and "none", and an "any" left with nothing is impossible.
    /// Null means the rule constrains nothing.
    /// </summary>
    private PlannedRule? PlanRule(string mode, IReadOnlyList<CatalogueTerm> terms, ref bool impossible)
    {
        var planned = new List<PlannedTerm>(terms.Count);
        foreach (var term in terms)
        {
            if (ResolveTerm(term) is { } resolved)
            {
                planned.Add(resolved);
            }
            else if (mode == CatalogueRules.All)
            {
                impossible = true;
                return null;
            }
        }

        if (planned.Count == 0)
        {
            impossible |= mode == CatalogueRules.Any;
            return null;
        }

        return new PlannedRule(mode, planned.ToArray());
    }

    private PlannedTerm? ResolveTerm(CatalogueTerm term)
    {
        if (term.Kind == CatalogueRules.Genre)
        {
            return vocabularies.Genres.TryGetValue(term.Name, out var genre)
                ? new PlannedTerm(genre, null, 0)
                : null;
        }

        int[]? ids = null;
        if (term.Subtags && vocabularies.TagSubtrees?.TryGetValue(term.Name, out var subtree) == true)
        {
            ids = subtree;
        }
        else if (vocabularies.Tags.TryGetValue(term.Name, out var named))
        {
            ids = [.. named];
            Array.Sort(ids);
        }

        return ids is { Length: > 0 }
            ? new PlannedTerm(-1, ids, term.Central ? TagMath.Defining : (byte)0)
            : null;
    }

    private bool Passes(int row, PlannedRule rule)
    {
        var genres = columns.Genres[row];
        var blob = columns.Tags[row];
        foreach (var term in rule.Terms)
        {
            var held = term.TagIds is { } tagIds
                ? TagMath.ContainsAny(blob, tagIds, term.MinClass)
                : genres.IndexOf(term.GenreId) >= 0;
            switch (rule.Mode)
            {
                case CatalogueRules.All when !held:
                case CatalogueRules.None when held:
                    return false;
                case CatalogueRules.Any when held:
                    return true;
            }
        }

        return rule.Mode != CatalogueRules.Any;
    }

    /// <summary>
    /// Whether a series this index does not hold carries a never-show term, judged on its own genres
    /// and tags from the dump. Terms resolve through this index's vocabularies, so a subtag or
    /// central-only term means what it means on a row; a tag name the index does not know still
    /// hides by name.
    /// </summary>
    public bool IsHidden(
        IReadOnlyList<CatalogueTerm> hidden, IReadOnlyCollection<string> genres,
        IReadOnlyList<(int Id, string Name, byte Class)> tags)
    {
        foreach (var term in hidden)
        {
            if (term.Kind == CatalogueRules.Genre)
            {
                if (genres.Contains(term.Name, StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            var ids = ResolveTerm(term)?.TagIds;
            var minClass = term.Central ? TagMath.Defining : TagMath.Unweighted;
            if (tags.Any(t => t.Class >= minClass &&
                    ((ids is not null && Array.BinarySearch(ids, t.Id) >= 0) ||
                     string.Equals(t.Name, term.Name, StringComparison.OrdinalIgnoreCase))))
            {
                return true;
            }
        }

        return false;
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
            if (TryGetRow(id, out var row))
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

        if (plan.Exclude is { } excluded && excluded[row])
        {
            return false;
        }

        if (plan.MinStartDay is int since && StartDayAt(row) is var day && (day == Unknown || day < since))
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
                if (rowGenres.IndexOf(g) < 0)
                {
                    return false;
                }
            }
        }

        if (plan.Tags is { } wantTags && !TagMath.ContainsAll(columns.Tags[row], wantTags))
        {
            return false;
        }

        if (plan.ContentRatings is { } wantRatings && Array.IndexOf(wantRatings, columns.ContentRatings[row]) < 0)
        {
            return false;
        }

        if (plan.Rules is { } rules)
        {
            foreach (var rule in rules)
            {
                if (!Passes(row, rule))
                {
                    return false;
                }
            }
        }

        return plan.Hidden is not { } hidden || Passes(row, hidden);
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

        // Rented, not allocated: a float per row is half a megabyte at catalogue scale, which is
        // three Large Object Heap allocations on every keystroke-driven search. The LOH is not
        // compacted, so allocating them grew the process for the life of the install.
        var scores = ScanPools.Floats.Rent(Count);
        var keys = ScanPools.Floats.Rent(Count);
        var values = ScanPools.Ints.Rent(Count);
        try
        {
            Parallel.For(
                0,
                Count,
                new ParallelOptions { CancellationToken = ct },
                row => scores[row] = Matches(row, plan)
                    ? CosineAt(row, packedQuery, queryScale)
                    : float.NegativeInfinity);

            // Collect the survivors and sort them rather than heap-selecting: at index sizes in the
            // low hundreds of thousands the sort is a few milliseconds and the code stays obvious.
            var found = 0;
            for (var row = 0; row < Count; row++)
            {
                if (!float.IsNegativeInfinity(scores[row]))
                {
                    values[found] = row;
                    keys[found] = -scores[row]; // ascending sort on the negation = descending by cosine
                    found++;
                }
            }

            // Bounded to the survivors explicitly: a rented array is longer than the data in it, and
            // sorting the tail would rank whatever the previous caller left there.
            Array.Sort(keys, values, 0, found);
            var result = new List<(int Row, float Cosine)>(Math.Min(take, found));
            for (var i = 0; i < found && i < take; i++)
            {
                result.Add((values[i], scores[values[i]]));
            }

            return result;
        }
        finally
        {
            ScanPools.Floats.Return(scores);
            ScanPools.Floats.Return(keys);
            ScanPools.Ints.Return(values);
        }
    }

    private static bool IsSorted(long[] ids)
    {
        for (var i = 1; i < ids.Length; i++)
        {
            if (ids[i] <= ids[i - 1])
            {
                return false;
            }
        }

        return true;
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
