using Maki.Api.Dtos;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>One of the reader's own series, as a cluster shows it.</summary>
public record TasteMember(int SeriesId, string Title, string? CoverUrl);

/// <summary>A catalogue title the reader does not own, named as an example of a region.</summary>
public record TasteRegionTitle(string ProviderId, string Title, int? Year);

/// <summary>
/// A neighbourhood next to one of the reader's groups that they own nothing in.
/// </summary>
/// <param name="Tags">What lives there that is rare in this reader's library.</param>
public record TasteBlindSpot(IReadOnlyList<string> Tags, IReadOnlyList<TasteRegionTitle> Examples);

/// <summary>
/// One of the distinct things a reader reads.
/// </summary>
/// <param name="DistinctiveTags">
/// What separates this group from the reader's <em>other</em> groups, not from the catalogue. A
/// reader whose whole library is romance gets groups distinguished by something other than romance.
/// </param>
/// <param name="Coherence">Mean cosine of the group's members to its own centre. Tight vs sprawling.</param>
/// <param name="SeedIds">The group's MangaBaka ids, so it can be recommended from on its own.</param>
public record TasteCluster(
    IReadOnlyList<string> DistinctiveTags,
    int Size,
    double Share,
    double Coherence,
    IReadOnlyList<TasteMember> Examples,
    IReadOnlyList<long> SeedIds,
    TasteBlindSpot? BlindSpot);

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
    IReadOnlyList<TasteCluster> Clusters,
    string? ClustersUnavailable,
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
/// Everything here needs the vectors and could not be produced by counting: which distinct things
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
    ILogger<TasteInsightsService> logger)
{
    /// <summary>
    /// Series the clustering will look at, most-engaged first. A cap because the vectors are
    /// materialized as floats and a very large library would otherwise hold the whole index's worth
    /// of them at once; well above any library this has been seen on.
    /// </summary>
    private const int MaxPoints = 1500;

    /// <summary>Members named per group. The medoid first, so the group has a face.</summary>
    private const int ExamplesPerCluster = 4;

    /// <summary>Tags used to label a group or a drift bucket.</summary>
    private const int LabelTags = 3;

    /// <summary>
    /// How far a candidate has to sit from everything the reader owns before it counts as unexplored.
    /// Above this it is the same feel as something on their shelf, which is a recommendation rather
    /// than a blind spot.
    /// </summary>
    private const double BlindSpotOwnedCeiling = 0.82;

    /// <summary>
    /// And how close to the group's centre it still has to be. Past this it is simply a different
    /// part of the catalogue, and calling it a gap in this reader's taste would be flattery.
    /// </summary>
    private const double BlindSpotFloor = 0.42;

    private const int BlindSpotScan = 300;

    /// <summary>
    /// How many of the nearest survivors actually make up the region.
    /// <para>
    /// The scan returns hundreds, and a label drawn from all of them describes the catalogue rather
    /// than the neighbourhood: across that many titles no tag is common enough to name anything. The
    /// nearest few dozen are what "next door" means.
    /// </para>
    /// </summary>
    private const int BlindSpotRegion = 60;

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
        DateTime? FirstReadAt);

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
                firstReadAt.TryGetValue(row.SeriesId, out var at) ? at : null));
        }

        var total = wanted.Count;
        if (points.Count < TasteClustering.MinPoints)
        {
            return Nothing(
                $"Needs at least {TasteClustering.MinPoints} series the catalogue knows about. "
                + $"So far this view has {points.Count}.",
                points.Count, total);
        }

        var started = DateTime.UtcNow;

        // Tag names for everything in play, in one dump read: the group labels, the drift labels and
        // the blind-spot labels all want them, and the dump is the only thing that has them.
        var tags = await store.GetProfileRowsAsync([.. points.Select(p => p.MangaBakaId)], ct);
        var tagsById = points.ToDictionary(
            p => p.MangaBakaId,
            p => tags.TryGetValue(p.MangaBakaId, out var row)
                ? row.Tags.Where(t => !t.IsSpoiler).Select(t => t.Name).ToArray()
                : []);

        var allTags = points.Select(p => tagsById[p.MangaBakaId]).ToList();

        // Drift is computed whatever the clustering does. They answer different questions off the
        // same points, and a library that refuses to divide can still have moved over time.
        var (drift, driftUnavailable) = Drift(points, tagsById);

        var clustered = TasteClustering.Cluster([.. points.Select(p => p.Vector)]);
        if (clustered is null)
        {
            return new TasteInsights(
                [], "Your reading did not split into distinct groups.", null, null,
                drift, driftUnavailable, points.Count, total, null, DateTime.UtcNow);
        }

        var owned = points.Select(p => p.Row).ToHashSet();
        var plan = index.Plan(new RecommendationFilters(
            ContentRatings: ContentRating.Allowed(scope.MaxContentRating)));

        // Candidates first, for every group, so the tag rows they need are one dump read rather
        // than one per group.
        var candidatesByCluster = new Dictionary<int, List<long>>();
        for (var c = 0; c < clustered.K; c++)
        {
            candidatesByCluster[c] = BlindSpotCandidates(
                index, clustered.Centroids[c], plan, owned, points, ct);
        }

        var regionRows = await store.GetProfileRowsAsync(
            [.. candidatesByCluster.Values.SelectMany(v => v).Distinct()], ct);

        var clusters = new List<TasteCluster>();
        for (var c = 0; c < clustered.K; c++)
        {
            var members = points.Where((_, i) => clustered.Assignments[i] == c).ToList();
            if (members.Count == 0)
            {
                continue;
            }

            var centroid = clustered.Centroids[c];
            var ranked = members
                .Select(m => (Member: m, Similarity: TasteClustering.Dot(m.Vector, centroid)))
                .OrderByDescending(x => x.Similarity)
                .ToList();

            var memberIds = members.Select(m => m.SeriesId).ToHashSet();
            clusters.Add(new TasteCluster(
                // Against the reader's OTHER series, not against a baseline this group is most of.
                // A group holding two thirds of the library dominates any all-library baseline, so
                // every one of its tags lifts to about 1 and it comes back nameless.
                DistinctiveTags: Distinctive(
                    TagShares([.. members.Select(m => tagsById[m.MangaBakaId])]),
                    TagShares([.. points.Where(p => !memberIds.Contains(p.SeriesId))
                        .Select(p => tagsById[p.MangaBakaId])])),
                Size: members.Count,
                Share: (double)members.Count / points.Count,
                Coherence: ranked.Average(x => x.Similarity),
                Examples: [.. ranked.Take(ExamplesPerCluster)
                    .Select(x => new TasteMember(x.Member.SeriesId, x.Member.Title, x.Member.CoverUrl))],
                SeedIds: [.. members.Select(m => m.MangaBakaId)],
                // Measured against THIS group, not the whole library: the region sits next to this
                // group specifically, and the question is what that group is missing.
                BlindSpot: BlindSpotFrom(
                    candidatesByCluster[c],
                    regionRows,
                    TagShares([.. members.Select(m => tagsById[m.MangaBakaId])]))));
        }

        var (oddOneOut, oddSimilarity) = OddOneOut(points, clustered);

        logger.LogInformation(
            "Built taste insights over {Points} series into {Clusters} group(s) in {Elapsed:F1}s",
            points.Count, clusters.Count, (DateTime.UtcNow - started).TotalSeconds);

        return new TasteInsights(
            clusters.OrderByDescending(c => c.Size).ToList(),
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

    /// <summary>
    /// The series least like the rest of the library: lowest cosine to its own group's centre, which
    /// is the nearest centre it has. Measured against its own group rather than the library mean so
    /// a reader with two genuine tastes does not simply get told their smaller taste is odd.
    /// </summary>
    private static (TasteMember?, double?) OddOneOut(
        List<Point> points, TasteClustering.Result clustered)
    {
        var worst = -1;
        var worstSimilarity = double.PositiveInfinity;
        for (var i = 0; i < points.Count; i++)
        {
            var similarity = TasteClustering.Dot(points[i].Vector, clustered.Centroids[clustered.Assignments[i]]);
            if (similarity < worstSimilarity)
            {
                worstSimilarity = similarity;
                worst = i;
            }
        }

        return worst < 0
            ? (null, null)
            : (new TasteMember(points[worst].SeriesId, points[worst].Title, points[worst].CoverUrl),
                worstSimilarity);
    }

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
                // Against the other quarters, for the same reason a group is labelled against the
                // other groups: what changed is the question, not what is common throughout.
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

    /// <summary>
    /// What sits beside a group that the reader owns nothing like.
    ///
    /// <para>
    /// One index scan from the group's centre, then two cuts: drop anything close enough to an owned
    /// series to be the same feel, and drop anything so far from the centre that calling it adjacent
    /// would be a stretch. What survives is named by the tags common in the region and rare in this
    /// reader's library, which is the part that makes it a blind spot rather than a suggestion.
    /// </para>
    /// </summary>
    private static List<long> BlindSpotCandidates(
        VectorIndex index,
        float[] centroid,
        FilterPlan plan,
        HashSet<int> owned,
        List<Point> points,
        CancellationToken ct)
    {
        var hits = index.Search(centroid, plan, BlindSpotScan, ct);
        var candidates = new List<long>();
        foreach (var (row, cosine) in hits)
        {
            if (owned.Contains(row) || cosine < BlindSpotFloor)
            {
                continue;
            }

            // Near anything already on the shelf is a recommendation, not a gap.
            if (!points.Any(p => index.CosineBetween(row, p.Row) >= BlindSpotOwnedCeiling))
            {
                candidates.Add(index.IdAt(row));
                if (candidates.Count >= BlindSpotRegion)
                {
                    break; // hits arrive nearest-first, so this keeps the closest of them
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// Names a region from its candidates. Those are series the reader does <em>not</em> own, so
    /// their tag rows come from a separate dump read: the library's own rows say nothing about them.
    /// </summary>
    private static TasteBlindSpot? BlindSpotFrom(
        IReadOnlyList<long> candidates,
        IReadOnlyDictionary<long, MangaBakaProfileRow> regionRows,
        Dictionary<string, double> groupTags)
    {
        if (candidates.Count < 3)
        {
            return null;
        }

        static string[] TagsOf(IReadOnlyDictionary<long, MangaBakaProfileRow> rows, long id) =>
            rows.TryGetValue(id, out var row)
                ? [.. row.Tags.Where(t => !t.IsSpoiler).Select(t => t.Name)]
                : [];

        var labels = Missing(
            TagShares([.. candidates.Select(id => TagsOf(regionRows, id))]), groupTags);
        if (labels.Count == 0)
        {
            return null;
        }

        // The three nearest the centre, named. Anything the dump has no row for is skipped rather
        // than shown as a bare id.
        var examples = candidates
            .Where(id => regionRows.TryGetValue(id, out var row) && !string.IsNullOrWhiteSpace(row.Title))
            .Take(3)
            .Select(id => new TasteRegionTitle(
                id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                regionRows[id].Title!,
                regionRows[id].Year))
            .ToList();

        return examples.Count == 0 ? null : new TasteBlindSpot(labels, examples);
    }

    /// <summary>
    /// What the neighbourhood has that the group does not.
    ///
    /// <para>
    /// Ranked by plain difference in coverage rather than by the lift <see cref="Distinctive"/>
    /// uses, and that is the whole point. The region is drawn from around the group's own centre, so
    /// by construction it shares the group's tags and every ratio lands near 1: measured that way a
    /// blind spot can never be found, whatever the thresholds. A difference asks the question that
    /// can actually be true — this is on most of what sits next door and on almost none of yours.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> Missing(
        Dictionary<string, double> region, Dictionary<string, double> group)
    {
        // Both floors are low on purpose, and were set against what the shares actually are rather
        // than what they feel like they should be. A neighbourhood of sixty titles drawn from one
        // centre is genuinely varied, and `tags_v2` is long-tailed: in practice no tag covers more
        // than about an eighth of such a region, so a floor of "a third", or even "a sixth", finds
        // nothing at all and the feature silently never fires. Only the top few by gap are shown,
        // which is what keeps a low floor from turning into noise.
        return [.. region
            .Where(kv => kv.Value >= 0.07) // roughly four titles of a sixty-title neighbourhood
            .Select(kv => (kv.Key, Gap: kv.Value - group.GetValueOrDefault(kv.Key)))
            .Where(x => x.Gap >= 0.05)
            .OrderByDescending(x => x.Gap)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Take(LabelTags)
            .Select(x => x.Key)];
    }

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
    /// than by frequency, which is the entire difference between "what this group is" and "what this
    /// reader likes": a reader whose every series is romance gets groups labelled by whatever is
    /// <em>not</em> romance.
    /// <para>
    /// <paramref name="rest"/> must exclude the subset itself. Comparing a set against a baseline it
    /// makes up most of drives every lift to 1 and returns nothing, which is exactly the case of the
    /// one big group that most readers have.
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
        int SeriesId, string Title, long MangaBakaId, string? CoverPath, DateTime? LastMetadataRefresh);

    private static async Task<List<LibraryRow>> LibraryRowsAsync(MakiDbContext db, CancellationToken ct) =>
        await db.Series
            .Where(s => s.MangaBakaId != null && s.Incognito != IncognitoMode.Full)
            .Select(s => new LibraryRow(
                s.Id, s.Title, (long)s.MangaBakaId!.Value, s.CoverPath, s.LastMetadataRefresh))
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
