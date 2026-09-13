using System.Globalization;
using Maki.Api.Dtos;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>One of the reader's own series, as a group shows it.</summary>
public record TasteMember(int SeriesId, string Title, string? CoverUrl);

/// <summary>
/// One of the specific, recurring things a reader reads.
///
/// <para>
/// Groups overlap: a book can be in "Fake Relationship" and in "Romance + Comedy" at once, which is
/// the whole reason these are mined rather than clustered. Nothing about a reader's library is a
/// partition of it.
/// </para>
/// </summary>
/// <param name="Label">The facets joined, which is the name the card shows.</param>
/// <param name="Tags">The same facets unjoined, for a caller that needs them apart.</param>
/// <param name="Coherence">Mean cosine of the group's members to its own centre. Tight vs sprawling.</param>
/// <param name="SeedIds">The group's MangaBaka ids, so it can be recommended from on its own.</param>
/// <param name="Picks">What the catalogue has in this group that the reader does not own.</param>
public record TasteGroup(
    string Label,
    IReadOnlyList<string> Tags,
    int Size,
    double Share,
    double Coherence,
    IReadOnlyList<TasteMember> Examples,
    IReadOnlyList<long> SeedIds,
    IReadOnlyList<MangaBakaRecommendation> Picks);

/// <summary>Where the reader's centre of gravity sat during one stretch of time.</summary>
/// <param name="SimilarityToStart">
/// Cosine of this bucket's centre against the earliest bucket's. Falling means they have moved.
/// </param>
public record TasteDriftPoint(
    string Bucket,
    int SeriesCount,
    double SimilarityToStart,
    double SimilarityToPrevious,
    IReadOnlyList<string> DistinctiveTags,
    TasteMember? Example);

/// <summary>
/// What the vectors say about a reader, as opposed to what counting their genres says.
/// </summary>
/// <param name="Unavailable">
/// Why there is nothing to show, when there is nothing to show. Null on success. Separate from an
/// error because every one of these is an ordinary state: no index yet, too few series, no history.
/// </param>
public record TasteInsights(
    IReadOnlyList<TasteGroup> Groups,
    string? GroupsUnavailable,
    TasteMember? OddOneOut,
    double? OddOneOutSimilarity,
    IReadOnlyList<TasteDriftPoint> Drift,
    string? DriftUnavailable,
    int Covered,
    int Total,
    string? Unavailable,
    DateTime GeneratedAt);

/// <summary>
/// The reader in the embedding space rather than in a tally.
///
/// <para>
/// Everything here needs the vectors and could not be produced by counting: which specific things
/// somebody reads, how tightly, which of their series is the odd one out, where their taste has
/// moved, and what sits next to them that they have never touched. The genre and tag composition
/// lives in <see cref="TasteProfileService"/> and on the Stats page, and is a different question.
/// </para>
/// </summary>
public class TasteInsightsService(
    IServiceScopeFactory scopeFactory,
    SeedWeightService seedWeights,
    BehavioralTasteService taste,
    MangaBakaLocalStore store,
    VectorIndexCache vectorIndex,
    EmbeddingStore embeddings,
    ILogger<TasteInsightsService> logger)
{
    /// <summary>
    /// Series the mining will look at, most-engaged first. A cap because the vectors are
    /// materialized as floats and a very large library would otherwise hold the whole index's worth
    /// of them at once; well above any library this has been seen on.
    /// </summary>
    private const int MaxPoints = 1500;

    /// <summary>Members named per group. The medoid first, so the group has a face.</summary>
    private const int ExamplesPerGroup = 4;

    /// <summary>Tags used to label a drift bucket or a blind spot.</summary>
    private const int LabelTags = 3;

    /// <summary>
    /// Which weight classes of a tag can name a group. A tag the dump marked incidental is a thing
    /// that happened in one chapter, and a group built on those is a group about nothing.
    /// </summary>
    private const byte MinTagClass = TagMath.Defining;

    /// <summary>
    /// The <c>name_path</c> roots a tag may name a group from.
    ///
    /// <para>
    /// Deliberately wider than <see cref="TagMath.IsStoryCategory"/>, which this used to call and
    /// which covers only 2471 of the vocabulary's 6427 tags. That set is tuned for a different job -
    /// constraining a recommendation scan in <c>SideInterestRailService</c>, where a creature tag
    /// drags in noise - and borrowing it here dropped "Monsters" (<c>Species &amp; Creatures</c>)
    /// and "Magic" (<c>World Building</c>) before the mining could see them. Naming a habit is the
    /// opposite problem from seeding a scan: those are exactly the words a reader recognises their
    /// own shelf by, and without them a progression-fantasy library could not even form "Monsters +
    /// Skills". A romance library never showed it, which is why it survived review.
    /// </para>
    ///
    /// <para>
    /// Still out, and for the same reason as before: <c>Character Types</c>, <c>Character Traits</c>
    /// and <c>Character Archetype</c> describe a cast, <c>Work Info</c> and
    /// <c>Audience Demographics</c> describe a print run, and <c>Sexual Content</c> is a content
    /// warning the rating filter already owns.
    /// </para>
    ///
    /// <para>
    /// <c>Locations</c> and <c>Narrative Tropes</c> were tried here and taken back out, which is
    /// worth recording because the case for them is good and the measurement is not. Locations holds
    /// "Dungeon" (df 820) and every reason to want it, but its head is "School" (40037), "High
    /// School" (6130) and "Japan" (5154): real-world backdrops whose catalogue rarity is an artifact
    /// of nobody bothering to tag them, not of the trait being rare. So they clear every specificity
    /// floor an IDF can express, and the shelf came back naming "Japan" and "High School + School
    /// Life" - the exact label this surface was built to stop printing. The premise tags a dungeon
    /// library actually needs are in <c>Settings &gt; Game Elements</c> and <c>Themes</c> already.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> GroupCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Themes", "Settings", "Relationship", "Activities", "Occupations",
        "Species & Creatures", "World Building",
    };

    /// <summary>
    /// Genres that say who a series was drawn for rather than what it is.
    ///
    /// <para>
    /// The tag side of this cut is <see cref="GroupCategories"/>, which leaves out the
    /// "Audience Demographics" root. Genres carry no category at all, so the same five names have to
    /// be listed here: half a manga shelf is shounen, and a card reading "Shounen" over forty-eight
    /// series is the broad, useless label this whole surface exists to replace.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> DemographicGenres = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shounen", "Shoujo", "Seinen", "Josei", "Kodomo",
    };

    /// <summary>
    /// Rows the per-group scan ranks before the owned ones are dropped. Comfortably above
    /// <see cref="PicksPerGroup"/> because a group's own members match its filter and rank near its
    /// centre by construction, so they take the head of the list.
    /// </summary>
    private const int GroupScan = 240;

    /// <summary>Picks on a group's rail.</summary>
    private const int PicksPerGroup = 15;

    /// <summary>
    /// Unowned rows kept per group before the cross-rail de-duplication picks from them.
    ///
    /// <para>
    /// Deeper than <see cref="PicksPerGroup"/> because a later rail gives up every title an earlier
    /// one already showed, and on a library whose groups genuinely sit near each other that is most
    /// of its head. Exhausting the pool is what makes a rail come back short, so this is sized to
    /// survive every earlier rail taking its best.
    /// </para>
    /// </summary>
    private const int PickPool = 60;

    /// <summary>Series a time bucket needs before its centre means anything.</summary>
    private const int MinBucketSeries = 3;

    private const int CacheSlots = 40;
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, (TasteInsights Insights, DateTime GeneratedAt)> _cache = [];

    /// <summary>One of the reader's series, joined up across the three places its parts live.</summary>
    private sealed record Point(
        int SeriesId,
        string Title,
        string? CoverUrl,
        long MangaBakaId,
        int Row,
        float[] Vector,
        DateTime? FirstReadAt,
        IReadOnlyList<string> Genres);

    /// <summary>
    /// A tag or a genre the reader's library carries, and how much of the catalogue does.
    /// </summary>
    /// <param name="Key">
    /// Case-folded and kind-prefixed, so "Isekai" the genre and "Isekai" the tag stay two facets:
    /// they filter through different columns and are not interchangeable.
    /// </param>
    private sealed record Facet(string Key, string Name, bool IsTag, double Specificity);

    public async Task<TasteInsights> GetAsync(
        ICurrentUser scope, TasteView view, bool refresh, CancellationToken ct = default)
    {
        var key = $"{scope.UserId}:{view}";
        await _lock.WaitAsync(ct);
        try
        {
            if (!refresh &&
                _cache.TryGetValue(key, out var hit) &&
                DateTime.UtcNow - hit.GeneratedAt < CacheFor)
            {
                return hit.Insights;
            }

            var insights = await BuildAsync(scope, view, ct);
            _cache[key] = (insights, DateTime.UtcNow);

            foreach (var stale in _cache
                         .Where(kv => DateTime.UtcNow - kv.Value.GeneratedAt >= CacheFor)
                         .Select(kv => kv.Key)
                         .ToList())
            {
                _cache.Remove(stale);
            }

            while (_cache.Count > CacheSlots)
            {
                _cache.Remove(_cache.MinBy(kv => kv.Value.GeneratedAt).Key);
            }

            return insights;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Nothing at all: no index, or nothing of this reader's the index knows about.</summary>
    private static TasteInsights Nothing(string why, int covered = 0, int total = 0) =>
        new([], null, null, null, [], null, covered, total, why, DateTime.UtcNow);

    private async Task<TasteInsights> BuildAsync(ICurrentUser scope, TasteView view, CancellationToken ct)
    {
        var index = await vectorIndex.GetAsync(ct);
        if (index is null || index.Count == 0)
        {
            return Nothing("The recommendation index has not been built yet.");
        }

        SeedWeights seeded;
        IReadOnlySet<long> readIds;
        List<LibraryRow> library;
        Dictionary<int, DateTime> firstReadAt;
        using (var dbScope = scopeFactory.CreateScope())
        {
            var db = dbScope.ServiceProvider.GetRequiredService<MakiDbContext>();
            db.Scope.SetUser(scope.UserId, scope.AllRootFolders);
            seeded = await seedWeights.BuildAsync(db, scope, ct);
            var signals = await taste.ReadSignalsAsync(db, scope.UserId, seeded.LibraryIds, ct);
            readIds = signals.Keys.ToHashSet();
            library = await LibraryRowsAsync(db, ct);
            firstReadAt = await FirstReadAtAsync(db, scope.UserId, ct);
        }

        var wanted = view == TasteView.Read
            ? library.Where(r => readIds.Contains(r.MangaBakaId)).ToList()
            : library;

        var points = new List<Point>(wanted.Count);
        var seen = new HashSet<long>();
        foreach (var row in wanted
                     .OrderByDescending(r => seeded.Weights.GetValueOrDefault(r.MangaBakaId, 1.0))
                     .ThenBy(r => r.SeriesId))
        {
            if (points.Count >= MaxPoints || !seen.Add(row.MangaBakaId))
            {
                continue;
            }

            if (!index.TryGetRow(row.MangaBakaId, out var indexRow))
            {
                continue; // owned, but outside the index's candidate set - nothing to place it by
            }

            points.Add(new Point(
                row.SeriesId, row.Title, SeriesDto.CoverUrlFor(row.SeriesId, row.CoverPath, row.LastMetadataRefresh),
                row.MangaBakaId, indexRow, index.VectorAt(indexRow),
                firstReadAt.TryGetValue(row.SeriesId, out var at) ? at : null,
                row.Genres));
        }

        var total = wanted.Count;
        if (points.Count < TasteGroupMining.MinPoints)
        {
            return Nothing(
                $"Needs at least {TasteGroupMining.MinPoints} series the catalogue knows about. "
                + $"So far this view has {points.Count}.",
                points.Count, total);
        }

        var started = DateTime.UtcNow;
        var vocab = embeddings.GetVocab();

        // Plain tag names per series, for the drift labels and the blind-spot ones. Same extraction
        // the facets use, minus the weight and category cuts: a label wants everything the series is
        // known for, where a group has to be built on something the series is actually about.
        var tagsById = points.ToDictionary(
            p => p.MangaBakaId,
            p => LabelTagsOf(index, vocab, p.Row));

        // Drift is computed whatever the mining does. They answer different questions off the same
        // points, and a library with no recurring facet can still have moved over time.
        var (drift, driftUnavailable) = Drift(points, tagsById);

        var facets = FacetsOf(index, vocab, points);
        var mined = TasteGroupMining.Mine(
            [.. points.Select((_, i) => (IReadOnlyList<TasteGroupMining.Facet>)
                [.. facets.PerPoint[i].Select(f => new TasteGroupMining.Facet(facets.KeyIds[f.Key], f.Specificity))])]);

        if (mined.Count == 0)
        {
            return new TasteInsights(
                [], "Nothing recurs across enough of your reading to name a group yet.", null, null,
                drift, driftUnavailable, points.Count, total, null, DateTime.UtcNow);
        }

        var groups = await GroupsAsync(scope, index, points, facets, mined, ct);
        var (oddOneOut, oddSimilarity) = OddOneOut(points, mined, groups.Centroids);

        logger.LogInformation(
            "Built taste insights over {Points} series into {Groups} group(s) in {Elapsed:F1}s",
            points.Count, groups.Groups.Count, (DateTime.UtcNow - started).TotalSeconds);

        return new TasteInsights(
            groups.Groups,
            null,
            oddOneOut,
            oddSimilarity,
            drift,
            driftUnavailable,
            points.Count,
            total,
            null,
            DateTime.UtcNow);
    }

    /// <summary>Every facet the mining can see, and the integer keys it wants them under.</summary>
    private sealed record FacetSet(
        IReadOnlyList<IReadOnlyList<Facet>> PerPoint,
        IReadOnlyDictionary<int, Facet> ById,
        IReadOnlyDictionary<string, int> KeyIds);

    /// <summary>
    /// What each of the reader's series can be grouped by, and how much of the catalogue shares it.
    ///
    /// <para>
    /// Tags come from the index's packed blobs rather than from the dump's flat list because the
    /// two cuts that make a group namable only exist there: the weight class, and the category. Only
    /// story categories qualify (<see cref="TagMath.IsStoryCategory"/>), which is what keeps a group
    /// from being called "Primarily Teen Cast" or "Full Colour" - those describe a cast and a print
    /// run, and a reader does not have a habit of them.
    /// </para>
    ///
    /// <para>
    /// Genres come from the library's own rows and are counted across the index for their document
    /// frequency, since nothing precomputes one. They are kept despite being far broader than any
    /// tag: "Romance + Comedy" is a real group and no single tag expresses it.
    /// </para>
    /// </summary>
    private static FacetSet FacetsOf(
        VectorIndex index, IReadOnlyDictionary<int, TagInfo> vocab, List<Point> points)
    {
        var names = new Dictionary<string, (string Name, bool IsTag, long Df)>(StringComparer.Ordinal);
        var perPointNames = new List<List<string>>(points.Count);

        var genreIds = GenreIdsOf(index, points);
        var genreDf = GenreDocumentFrequencies(index, genreIds);

        foreach (var point in points)
        {
            var keys = new List<string>();
            foreach (var (id, cls) in TagMath.Unpack(index.TagsAt(point.Row)))
            {
                if (cls < MinTagClass ||
                    !vocab.TryGetValue(id, out var info) ||
                    info.IsSpoiler ||
                    !GroupCategories.Contains(info.Category) ||
                    string.IsNullOrWhiteSpace(info.Name))
                {
                    continue;
                }

                var key = "#" + info.Name.ToLowerInvariant();
                // Casing variants are interned as separate vocabulary ids carrying separate counts.
                // The largest is the one that saw the whole catalogue.
                names[key] = names.TryGetValue(key, out var seenTag)
                    ? seenTag with { Df = Math.Max(seenTag.Df, info.SeriesCount) }
                    : (info.Name, true, info.SeriesCount);
                keys.Add(key);
            }

            perPointNames.Add(keys);
        }

        // Genres second, and only the names no tag already claimed. The two vocabularies overlap -
        // "School Life" is both - and keying them separately is right (they filter through different
        // columns) but naming them separately is not: it mined a pair reading "School Life + School
        // Life", which is one facet wearing two hats.
        var tagNames = names.Values.Where(v => v.IsTag).Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < points.Count; i++)
        {
            foreach (var genre in points[i].Genres)
            {
                var trimmed = genre.Trim();
                if (trimmed.Length == 0 ||
                    tagNames.Contains(trimmed) ||
                    DemographicGenres.Contains(trimmed) ||
                    !genreIds.TryGetValue(trimmed, out var genreId) ||
                    !genreDf.TryGetValue(genreId, out var df))
                {
                    continue; // not in the index's vocabulary, so nothing could be filtered by it
                }

                var key = "@" + trimmed.ToLowerInvariant();
                names.TryAdd(key, (trimmed, false, df));
                perPointNames[i].Add(key);
            }
        }

        // The corpus is whichever is larger. A tag's SeriesCount comes from the dump and the index
        // is a filtered subset of it, so taking the index's row count alone would drive the commonest
        // tags to a negative IDF.
        var corpus = Math.Max(index.Count, names.Values.Select(v => v.Df).DefaultIfEmpty(0).Max() + 1);

        // Keys numbered in sorted order, not first-seen order: the mining breaks its ties on them, so
        // the same library has to produce the same numbering on every rebuild.
        var keyIds = names.Keys
            .Order(StringComparer.Ordinal)
            .Select((key, i) => (key, i))
            .ToDictionary(x => x.key, x => x.i);

        var facets = names.ToDictionary(
            kv => kv.Key,
            kv => new Facet(
                kv.Key,
                kv.Value.Name,
                kv.Value.IsTag,
                Math.Log((double)corpus / Math.Clamp(kv.Value.Df, 1, corpus - 1))));

        return new FacetSet(
            [.. perPointNames.Select(keys => (IReadOnlyList<Facet>)[.. keys.Distinct(StringComparer.Ordinal).Select(k => facets[k])])],
            facets.Values.ToDictionary(f => keyIds[f.Key]),
            keyIds);
    }

    /// <summary>The library's genre names resolved against the index's vocabulary, once.</summary>
    private static Dictionary<string, int> GenreIdsOf(VectorIndex index, List<Point> points)
    {
        var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in points.SelectMany(p => p.Genres).Select(g => g.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (name.Length > 0 && index.TryGetGenreId(name, out var id))
            {
                ids[name] = id;
            }
        }

        return ids;
    }

    /// <summary>
    /// How much of the catalogue carries each genre the reader has. One pass over the index's genre
    /// column, which nothing precomputes - unlike a tag, whose count the vocabulary already carries.
    /// </summary>
    private static Dictionary<int, long> GenreDocumentFrequencies(
        VectorIndex index, Dictionary<string, int> genreIds)
    {
        var counts = genreIds.Values.Distinct().ToDictionary(id => id, _ => 0L);
        if (counts.Count == 0)
        {
            return counts;
        }

        for (var row = 0; row < index.Count; row++)
        {
            foreach (var id in index.GenresAt(row))
            {
                if (counts.ContainsKey(id))
                {
                    counts[id]++;
                }
            }
        }

        return counts;
    }

    private sealed record BuiltGroups(IReadOnlyList<TasteGroup> Groups, IReadOnlyList<float[]> Centroids);

    /// <summary>
    /// Turns mined member sets into cards: a centre, a face, and a rail of what the catalogue has in
    /// the group that the reader does not.
    ///
    /// <para>
    /// One index scan per group, filtered to the group's own facets, so a pick has to actually be in
    /// the group rather than merely near it - the centroid alone would drift a "Fake Relationship"
    /// rail into general romance within a few slots.
    /// </para>
    /// </summary>
    private async Task<BuiltGroups> GroupsAsync(
        ICurrentUser scope,
        VectorIndex index,
        List<Point> points,
        FacetSet facets,
        IReadOnlyList<TasteGroupMining.Group> mined,
        CancellationToken ct)
    {
        var owned = points.Select(p => p.Row).ToHashSet();
        var allowed = ContentRating.Allowed(scope.MaxContentRating);

        var scans = new List<(TasteGroupMining.Group Mined, float[] Centroid, List<int> Picks)>();
        foreach (var group in mined)
        {
            var members = group.Members.Select(i => points[i]).ToList();
            var centroid = TasteClustering.Centroid([.. members.Select(m => m.Vector)]);
            if (centroid is null)
            {
                continue;
            }

            var names = group.Keys.Select(k => facets.ById[k]).ToList();
            var plan = index.Plan(new RecommendationFilters(
                Genres: names.Where(f => !f.IsTag).Select(f => f.Name).ToList() is { Count: > 0 } genres ? genres : null,
                Tags: names.Where(f => f.IsTag).Select(f => f.Name).ToList() is { Count: > 0 } tags ? tags : null,
                ContentRatings: allowed,
                MinChapters: 5));

            scans.Add((group, centroid, plan.Impossible ? [] : Scan(index, centroid, plan, owned, ct)));
        }

        // Every card's cards in one dump read. Hydration is the expensive half of this and the
        // groups overlap, so a read per group would fetch the same rows a dozen times.
        var hydrated = (await store.GetByIdsAsync(
                [.. scans.SelectMany(s => s.Picks).Select(index.IdAt).Distinct()],
                allowed, ct))
            .Where(item => long.TryParse(item.ProviderId, out _) && !string.IsNullOrWhiteSpace(item.Title))
            .ToDictionary(item => long.Parse(item.ProviderId, CultureInfo.InvariantCulture));

        // One title, one rail. Groups that sit near each other draw the same catalogue rows, and the
        // page then reads as twelve versions of one recommendation however different the member sets
        // are. Claimed in group order, so the strongest group gets first call on a title it shares -
        // the same rule SideInterestRailService's rows follow for the same reason.
        var claimed = new HashSet<long>();

        var groups = new List<TasteGroup>(scans.Count);
        var centroids = new List<float[]>(scans.Count);
        foreach (var (group, centroid, picks) in scans)
        {
            var members = group.Members.Select(i => points[i]).ToList();
            var ranked = members
                .Select(m => (Member: m, Similarity: TasteClustering.Dot(m.Vector, centroid)))
                .OrderByDescending(x => x.Similarity)
                .ThenBy(x => x.Member.SeriesId)
                .ToList();

            var names = group.Keys.Select(k => facets.ById[k].Name).ToList();
            groups.Add(new TasteGroup(
                Label: string.Join(" + ", names),
                Tags: names,
                Size: members.Count,
                Share: (double)members.Count / points.Count,
                Coherence: ranked.Average(x => x.Similarity),
                Examples: [.. ranked.Take(ExamplesPerGroup)
                    .Select(x => new TasteMember(x.Member.SeriesId, x.Member.Title, x.Member.CoverUrl))],
                SeedIds: [.. members.Select(m => m.MangaBakaId)],
                Picks: Claim(picks, index, hydrated, claimed)));
            centroids.Add(centroid);
        }

        return new BuiltGroups(groups, centroids);
    }

    /// <summary>
    /// A rail's titles, skipping anything an earlier rail already showed and stopping at
    /// <see cref="PicksPerGroup"/>. Mutates <paramref name="claimed"/>, which is the point: it is
    /// the running set of what the page has spent.
    /// </summary>
    private static List<MangaBakaRecommendation> Claim(
        List<int> picks,
        VectorIndex index,
        IReadOnlyDictionary<long, MangaBakaRecommendation> hydrated,
        HashSet<long> claimed)
    {
        var rail = new List<MangaBakaRecommendation>(PicksPerGroup);
        foreach (var row in picks)
        {
            var id = index.IdAt(row);
            if (claimed.Contains(id) || !hydrated.TryGetValue(id, out var item))
            {
                continue;
            }

            claimed.Add(id);
            rail.Add(item);
            if (rail.Count == PicksPerGroup)
            {
                break;
            }
        }

        return rail;
    }

    /// <summary>
    /// The nearest rows to a group's centre that the reader does not already own, deepest-first up
    /// to <see cref="PickPool"/>. Hits arrive nearest-first, so this keeps the closest of them.
    /// </summary>
    private static List<int> Scan(
        VectorIndex index, float[] centroid, FilterPlan plan, HashSet<int> owned, CancellationToken ct)
    {
        var picks = new List<int>();
        foreach (var (row, _) in index.Search(centroid, plan, GroupScan, ct))
        {
            if (!owned.Contains(row))
            {
                picks.Add(row);
                if (picks.Count == PickPool)
                {
                    break;
                }
            }
        }

        return picks;
    }

    /// <summary>
    /// The series least like the rest of the library. Drawn from what no group claimed, because with
    /// overlapping groups that set is the answer to the question the card asks; when every series
    /// landed in a group it falls back to the one furthest from all of their centres.
    /// </summary>
    private static (TasteMember?, double?) OddOneOut(
        List<Point> points,
        IReadOnlyList<TasteGroupMining.Group> mined,
        IReadOnlyList<float[]> centroids)
    {
        if (centroids.Count == 0)
        {
            return (null, null);
        }

        var claimed = mined.SelectMany(g => g.Members).ToHashSet();
        var pool = Enumerable.Range(0, points.Count).Where(i => !claimed.Contains(i)).ToList();
        if (pool.Count == 0)
        {
            pool = [.. Enumerable.Range(0, points.Count)];
        }

        var worst = pool
            .Select(i => (Index: i, Similarity: centroids.Max(c => TasteClustering.Dot(points[i].Vector, c))))
            .OrderBy(x => x.Similarity)
            .ThenBy(x => points[x.Index].SeriesId)
            .First();

        var point = points[worst.Index];
        return (new TasteMember(point.SeriesId, point.Title, point.CoverUrl), worst.Similarity);
    }

    /// <summary>
    /// Every non-spoiler tag name on a row, whatever its weight or category. Labels want the whole
    /// list; <see cref="FacetsOf"/> is the one that has to be picky.
    /// </summary>
    private static string[] LabelTagsOf(
        VectorIndex index, IReadOnlyDictionary<int, TagInfo> vocab, int row) =>
        [.. TagMath.Unpack(index.TagsAt(row))
            .Select(t => vocab.TryGetValue(t.Id, out var info) ? info : null)
            .Where(info => info is { IsSpoiler: false, Name.Length: > 0 })
            .Select(info => info!.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Where the reader's centre sat, quarter by quarter.
    ///
    /// <para>
    /// Dated by when each series was first read, not by its release year. Kavita-imported rows are
    /// excluded upstream by <see cref="FirstReadAtAsync"/>: an import stamps a whole back catalogue
    /// with one date, which would show as a reader who discovered everything they own on a Tuesday.
    /// </para>
    /// </summary>
    private static (IReadOnlyList<TasteDriftPoint>, string?) Drift(
        List<Point> points,
        Dictionary<long, string[]> tagsById)
    {
        var dated = points.Where(p => p.FirstReadAt is not null).ToList();
        if (dated.Count < MinBucketSeries * 2)
        {
            return ([], "Needs more dated reading history. Kavita imports do not count, since they all carry one date.");
        }

        var buckets = dated
            .GroupBy(p => Quarter(p.FirstReadAt!.Value))
            .Where(g => g.Count() >= MinBucketSeries)
            .OrderBy(g => g.Key)
            .ToList();

        if (buckets.Count < 2)
        {
            return ([], "Needs reading spread across at least two quarters.");
        }

        var centres = buckets
            .Select(b => (Bucket: b.Key, Members: b.ToList(), Centre: TasteClustering.Centroid([.. b.Select(p => p.Vector)])))
            .Where(x => x.Centre is not null)
            .ToList();

        if (centres.Count < 2)
        {
            return ([], "Needs reading spread across at least two quarters.");
        }

        var drift = new List<TasteDriftPoint>(centres.Count);
        for (var i = 0; i < centres.Count; i++)
        {
            var (bucket, members, centre) = centres[i];
            var medoid = members
                .OrderByDescending(m => TasteClustering.Dot(m.Vector, centre!))
                .First();

            drift.Add(new TasteDriftPoint(
                Bucket: bucket,
                SeriesCount: members.Count,
                SimilarityToStart: TasteClustering.Dot(centre!, centres[0].Centre!),
                SimilarityToPrevious: i == 0 ? 1 : TasteClustering.Dot(centre!, centres[i - 1].Centre!),
                // Against the other quarters: what changed is the question, not what is common
                // throughout.
                DistinctiveTags: Distinctive(
                    TagShares([.. members.Select(m => tagsById[m.MangaBakaId])]),
                    TagShares([.. centres
                        .Where((_, other) => other != i)
                        .SelectMany(x => x.Members)
                        .Select(p => tagsById[p.MangaBakaId])])),
                Example: new TasteMember(medoid.SeriesId, medoid.Title, medoid.CoverUrl)));
        }

        return (drift, null);
    }

    private static string Quarter(DateTime at) =>
        $"{at.Year} Q{(at.Month - 1) / 3 + 1}";

    /// <summary>Share of a set of series carrying each tag.</summary>
    private static Dictionary<string, double> TagShares(IReadOnlyList<string[]> perSeries)
    {
        var counts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (perSeries.Count == 0)
        {
            return counts;
        }

        foreach (var tags in perSeries)
        {
            foreach (var tag in tags.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                counts[tag] = counts.GetValueOrDefault(tag) + 1;
            }
        }

        foreach (var tag in counts.Keys.ToList())
        {
            counts[tag] /= perSeries.Count;
        }

        return counts;
    }

    /// <summary>
    /// The tags that separate one set from the rest. Ranked by lift over the comparison set rather
    /// than by frequency, which is the entire difference between "what this stretch was" and "what
    /// this reader likes": a reader whose every series is romance gets quarters labelled by whatever
    /// is <em>not</em> romance.
    /// <para>
    /// <paramref name="rest"/> must exclude the subset itself. Comparing a set against a baseline it
    /// makes up most of drives every lift to 1 and returns nothing.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> Distinctive(
        Dictionary<string, double> subset, Dictionary<string, double> rest)
    {
        return [.. subset
            .Where(kv => kv.Value >= 0.34) // present in a third of the set, or it labels nothing
            .Select(kv => (kv.Key, Lift: kv.Value / Math.Max(rest.GetValueOrDefault(kv.Key), 0.02)))
            .Where(x => x.Lift > 1.15)
            .OrderByDescending(x => x.Lift)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Take(LabelTags)
            .Select(x => x.Key)];
    }

    private sealed record LibraryRow(
        int SeriesId,
        string Title,
        long MangaBakaId,
        string? CoverPath,
        DateTime? LastMetadataRefresh,
        List<string> Genres);

    private static async Task<List<LibraryRow>> LibraryRowsAsync(MakiDbContext db, CancellationToken ct) =>
        await db.Series
            .Where(s => s.MangaBakaId != null && s.Incognito != IncognitoMode.Full)
            .Select(s => new LibraryRow(
                s.Id, s.Title, (long)s.MangaBakaId!.Value, s.CoverPath, s.LastMetadataRefresh, s.Genres))
            .ToListAsync(ct);

    /// <summary>
    /// When each series was first read, for the drift buckets.
    ///
    /// <para>
    /// Kavita-imported rows are excluded on the marker they carry (<c>Completed</c> with a zero
    /// <c>PageCount</c>): the import knows what was read but not when, so it dates a whole back
    /// catalogue to the day of the import. Left in, a reader who imported once would appear to have
    /// formed their entire taste in a single quarter.
    /// </para>
    /// </summary>
    private static async Task<Dictionary<int, DateTime>> FirstReadAtAsync(
        MakiDbContext db, int userId, CancellationToken ct) =>
        await db.ChapterProgress.IgnoreQueryFilters()
            .Where(p => p.UserId == userId && p.Completed && !p.Watched && p.PageCount > 0)
            .GroupBy(p => p.SeriesId)
            .Select(g => new { SeriesId = g.Key, At = g.Min(p => p.UpdatedAt) })
            .ToDictionaryAsync(x => x.SeriesId, x => x.At, ct);
}
