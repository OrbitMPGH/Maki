using Maki.Core.Security;
using Maki.Data;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.ReaderCohorts;

namespace Maki.Api.Services;

/// <summary>
/// Which population a profile describes. Both views weight a series the same way, with the weight
/// the recommender seeds with; they differ only in which series are counted.
/// </summary>
public enum TasteView
{
    /// <summary>Only series the user has reading history for.</summary>
    Read,

    /// <summary>The whole library. An unread series is present at the neutral weight, not absent.</summary>
    Shelf,
}

/// <summary>One thing the reader is into, and how much.</summary>
/// <param name="Share">This facet's slice of the view's total weight, 0..1.</param>
/// <param name="Support">Distinct series in the view carrying it. Below a floor, no ratio is offered.</param>
/// <param name="OverIndexShelf">
/// Weighted share here against the facet's flat share of the whole library. Above 1 means the
/// reader reaches for it more than simply owning it would predict. Null when support is too thin
/// for the ratio to mean anything.
/// </param>
/// <param name="OverIndexCatalogue">
/// The same against the MangaBaka catalogue, weighted toward titles more people read. Null when the
/// vector index is not built, and for facets the index has no vocabulary entry for.
/// </param>
public record TasteFacet(
    string Name,
    double Weight,
    double Share,
    int Support,
    double? OverIndexShelf,
    double? OverIndexCatalogue);

/// <summary>One release year and how much of the view sits on it.</summary>
public record TasteYearFacet(int Year, double Weight, double Share);

/// <summary>
/// A reader's own aggregate taste. <paramref name="SeriesCount"/> is how many series the view is
/// built from, which is the honest caveat on everything else in here.
/// </summary>
public record TasteProfile(
    IReadOnlyList<TasteFacet> Creators,
    IReadOnlyList<TasteFacet> Genres,
    IReadOnlyList<TasteFacet> Tags,
    IReadOnlyList<TasteFacet> Types,
    IReadOnlyList<TasteYearFacet> Years,
    int SeriesCount,
    int LibraryCount,
    bool CatalogueBaselineAvailable,
    /// <summary>
    /// Which population <c>OverIndexCatalogue</c> was weighted by: <c>readers</c> when the reader
    /// cohorts are installed, <c>popularity</c> when only the rank proxy is available, null when
    /// there is no baseline at all. Two facts that fail independently — the vector index can be
    /// built while the cohort artifact is absent — so the boolean alone could not express it.
    /// </summary>
    string? CatalogueBaselineSource,
    DateTime GeneratedAt);

/// <summary>
/// The reader's own taste, aggregated from the same weights that steer their recommendations.
///
/// <para>
/// Strictly personal. There is no user parameter anywhere on this path and deliberately no
/// <c>UserViewResolver</c>: an admin can read another user's activity stats, but "you over-index on
/// isekai" is not a fact anybody else needs.
/// </para>
/// </summary>
public class TasteProfileService(
    IServiceScopeFactory scopeFactory,
    SeedWeightService seedWeights,
    BehavioralTasteService taste,
    MangaBakaLocalStore store,
    VectorIndexCache vectorIndex,
    ReaderCohortCache readerCohorts,
    ILogger<TasteProfileService> logger)
{
    /// <summary>
    /// Series a facet must appear on before its over-index ratio is offered at all. A tag on one
    /// series can produce a ratio of twelve, which says nothing about the reader and everything
    /// about that series' tag list. Three is the smallest count where the number is not one row.
    /// </summary>
    private const int MinSupport = 3;

    /// <summary>How many of each facet the response carries. The UI shows a shorter head.</summary>
    private const int TopN = 20;

    /// <summary>
    /// Two per user, and an entry is a handful of small dictionaries rather than
    /// <c>RecommendationService</c>'s 200 hydrated recommendations, so this can afford more slots
    /// than that cache does.
    /// </summary>
    private const int CacheSlots = 40;

    /// <summary>
    /// Short next to the recommendation pool's twelve hours: the whole point of the page is that it
    /// reflects what you have been reading, and a profile that ignores this afternoon reads as
    /// broken. The work behind a miss is one library query and one dump query, not an index scan.
    /// </summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);

    /// <summary>The catalogue only moves when the dump is replaced, which is nightly at most.</summary>
    private static readonly TimeSpan CatalogueBaselineFor = TimeSpan.FromHours(24);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, (TasteProfile Profile, DateTime GeneratedAt)> _cache = [];

    // Instance-wide, not per user, and behind its own lock: it is the same answer for everybody, and
    // building it must not block a request that already has one.
    private readonly SemaphoreSlim _catalogueLock = new(1, 1);
    private (CatalogueBaseline Baseline, DateTime BuiltAt)? _catalogue;

    /// <param name="scope">
    /// The caller, and the only reader this can describe. Applied to the child scope this opens: a
    /// singleton creating its own scope gets an unrestricted <see cref="DataScope"/>, which would
    /// build the profile from root folders the caller was never granted.
    /// </param>
    public async Task<TasteProfile> GetAsync(
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
                return hit.Profile;
            }

            var profile = await BuildAsync(scope, view, ct);
            _cache[key] = (profile, DateTime.UtcNow);
            Evict();
            return profile;
        }
        finally
        {
            _lock.Release();
        }
    }

    private void Evict()
    {
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
    }

    private async Task<TasteProfile> BuildAsync(ICurrentUser scope, TasteView view, CancellationToken ct)
    {
        SeedWeights seeded;
        IReadOnlySet<long> readIds;
        using (var dbScope = scopeFactory.CreateScope())
        {
            var db = dbScope.ServiceProvider.GetRequiredService<MakiDbContext>();
            db.Scope.SetUser(scope.UserId, scope.AllRootFolders);
            seeded = await seedWeights.BuildAsync(db, scope, ct);
            // The raw read set, not the weights: a series whose reading happens to imply a neutral
            // weight was still read, and belongs in the Read population.
            var signals = await taste.ReadSignalsAsync(db, scope.UserId, seeded.LibraryIds, ct);
            readIds = signals.Keys.ToHashSet();
        }

        if (seeded.LibraryIds.Count == 0)
        {
            return new TasteProfile([], [], [], [], [], 0, 0, false, null, DateTime.UtcNow);
        }

        // One dump round trip covers both the view's numerator and the library denominator, whichever
        // view was asked for.
        var rows = await store.GetProfileRowsAsync(seeded.LibraryIds, ct);

        var population = view == TasteView.Read
            ? seeded.LibraryIds.Where(readIds.Contains).ToList()
            : seeded.LibraryIds.ToList();

        var profile = Aggregate(population, rows, seeded.Weights);
        // Flat: every owned series counts once, whatever the reader did with it. That is what makes
        // the ratio read as "more than owning it would predict".
        var baseline = Aggregate(seeded.LibraryIds, rows, weights: null);
        var catalogue = await GetCatalogueBaselineAsync(ct);

        return new TasteProfile(
            // No catalogue baseline for creators: the index interns author names as ids, and
            // resolving the whole catalogue back to names buys a number that says less than "on N of
            // your M series" already does.
            Creators: Facets(profile.Creators, profile.CreatorSupport, baseline.Creators, null),
            Genres: Facets(profile.Genres, profile.GenreSupport, baseline.Genres, catalogue?.GenreShare),
            Tags: Facets(profile.Tags, profile.TagSupport, baseline.Tags, catalogue?.TagShare),
            // No catalogue baseline for types either: the index carries no type column at all, and
            // "you read more manhwa than the catalogue holds" is a fact about the catalogue.
            Types: Facets(profile.Types, profile.TypeSupport, baseline.Types, null),
            Years: Years(profile.Years),
            SeriesCount: profile.SeriesCount,
            LibraryCount: seeded.LibraryIds.Count,
            CatalogueBaselineAvailable: catalogue is not null,
            CatalogueBaselineSource: catalogue?.Source,
            GeneratedAt: DateTime.UtcNow);
    }

    private static IReadOnlyList<TasteFacet> Facets(
        Dictionary<string, double> weights,
        Dictionary<string, int> support,
        Dictionary<string, double> shelfWeights,
        IReadOnlyDictionary<string, double>? catalogueShare)
    {
        var total = weights.Values.Sum();
        var shelfTotal = shelfWeights.Values.Sum();
        if (total <= 0)
        {
            return [];
        }

        return weights
            .Select(kv =>
            {
                var share = kv.Value / total;
                var enoughSupport = support.GetValueOrDefault(kv.Key) >= MinSupport;

                double? shelf = null;
                if (enoughSupport && shelfTotal > 0)
                {
                    var shelfShare = shelfWeights.GetValueOrDefault(kv.Key) / shelfTotal;
                    shelf = shelfShare > 0 ? share / shelfShare : null;
                }

                double? catalogue = null;
                if (enoughSupport && catalogueShare is not null &&
                    catalogueShare.TryGetValue(kv.Key, out var catShare) && catShare > 0)
                {
                    catalogue = share / catShare;
                }

                return new TasteFacet(kv.Key, kv.Value, share, support.GetValueOrDefault(kv.Key), shelf, catalogue);
            })
            .OrderByDescending(f => f.Weight)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Take(TopN)
            .ToList();
    }

    private static IReadOnlyList<TasteYearFacet> Years(Dictionary<string, double> years)
    {
        var total = years.Values.Sum();
        if (total <= 0)
        {
            return [];
        }

        return years
            .Select(kv => new TasteYearFacet(int.Parse(kv.Key), kv.Value, kv.Value / total))
            .OrderBy(y => y.Year)
            .ToList();
    }

    /// <summary>
    /// Weighted shares per facet, plus how many distinct series carry each one.
    /// <para>
    /// <paramref name="weights"/> null means every series counts 1.0, which is the library baseline.
    /// Otherwise a missing entry is the neutral 1.0 the recommender assumes, so an unread series in
    /// the Shelf view is present rather than absent.
    /// </para>
    /// </summary>
    private static Aggregated Aggregate(
        IReadOnlyList<long> ids,
        IReadOnlyDictionary<long, MangaBakaProfileRow> rows,
        IReadOnlyDictionary<long, double>? weights)
    {
        var result = new Aggregated();
        var total = 0.0;
        var counted = new List<(MangaBakaProfileRow Row, double Weight)>();

        foreach (var id in ids)
        {
            if (!rows.TryGetValue(id, out var row))
            {
                continue; // in the library, not in the dump: it can contribute nothing either way
            }

            var weight = weights is null ? 1.0 : Math.Max(0, weights.GetValueOrDefault(id, 1.0));
            counted.Add((row, weight));
            total += weight;
        }

        result.SeriesCount = counted.Count;
        if (total <= 0)
        {
            return result;
        }

        foreach (var (row, weight) in counted)
        {
            var share = weight / total;

            foreach (var genre in Distinct(row.Genres))
            {
                Add(result.Genres, result.GenreSupport, genre, share);
            }

            foreach (var tag in row.Tags)
            {
                // Spoiler flags are per series, not per tag name, which is why this has to be read
                // off the row rather than filtered against any global list.
                if (tag.IsSpoiler)
                {
                    continue;
                }

                Add(result.Tags, result.TagSupport, tag.Name, share * BucketWeight(tag.Weight));
            }

            // Both roles, one person: someone drawing and writing the same series is one credit, not
            // two. Sentinels ("Various", an imprint standing in for a person) are dropped on the same
            // rule the recommender's author channel uses, so a name filtered on one side and kept on
            // the other cannot exist.
            foreach (var credit in Distinct(row.Authors.Concat(row.Artists)).Where(CreditNames.IsPerson))
            {
                Add(result.Creators, result.CreatorSupport, credit, share);
            }

            if (!string.IsNullOrWhiteSpace(row.Type))
            {
                Add(result.Types, result.TypeSupport, row.Type, share);
            }

            if (row.Year is > 0)
            {
                Add(result.Years, null, row.Year.Value.ToString(), share);
            }
        }

        return result;

        static void Add(Dictionary<string, double> bucket, Dictionary<string, int>? support, string key, double share)
        {
            bucket[key] = bucket.GetValueOrDefault(key) + share;
            if (support is not null)
            {
                support[key] = support.GetValueOrDefault(key) + 1;
            }
        }

        static IEnumerable<string> Distinct(IEnumerable<string> values) =>
            values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// How much a tag counts for, by the bucket MangaBaka filed it under. A "core" tag is what the
    /// series is about; an "incidental" one is a thing that happens in it once.
    /// <para>
    /// Derived from <see cref="MangaBakaTag.Rank"/> rather than repeating the bucket names, so the
    /// vocabulary and its order live in one place: the top rank scores 1.0 and each step down is
    /// worth a quarter less, which is the same 1.0/0.75/0.5/0.25 this always used.
    /// </para>
    /// </summary>
    private static double BucketWeight(string bucket) => 1.0 - (0.25 * MangaBakaTag.Rank(bucket));

    /// <summary>
    /// How common each genre and tag is across the catalogue, weighted toward titles more people
    /// read. Null when the vector index is not built, which is the normal state of a fresh install
    /// and must cost the rest of the page nothing.
    /// </summary>
    private async Task<CatalogueBaseline?> GetCatalogueBaselineAsync(CancellationToken ct)
    {
        var cohorts = await readerCohorts.GetAsync(ct);

        await _catalogueLock.WaitAsync(ct);
        try
        {
            // Keyed on the cohort artifact's own stamp as well as on age. Installing the artifact
            // changes what this baseline MEANS, and a 24-hour TTL would otherwise keep serving the
            // popularity proxy for a day while the page claimed real readership.
            if (_catalogue is { } cached
                && DateTime.UtcNow - cached.BuiltAt < CatalogueBaselineFor
                && cached.Baseline.CohortsAt == cohorts?.GeneratedAt)
            {
                return cached.Baseline;
            }

            var index = await vectorIndex.GetAsync(ct);
            if (index is null || index.Count == 0)
            {
                return null;
            }

            var started = DateTime.UtcNow;
            var built = BuildCatalogueBaseline(index, cohorts);
            _catalogue = (built, DateTime.UtcNow);
            logger.LogInformation(
                "Built taste catalogue baseline over {Rows} rows from {Source} in {Elapsed:F1}s",
                index.Count, built.Source, (DateTime.UtcNow - started).TotalSeconds);
            return built;
        }
        finally
        {
            _catalogueLock.Release();
        }
    }

    /// <summary>
    /// One linear pass over the index's resident arrays. Keyed by the index's own interned ids, so
    /// nothing has to be resolved backwards from an id to a name: the caller looks its facet names
    /// up forwards instead.
    /// </summary>
    private static CatalogueBaseline BuildCatalogueBaseline(VectorIndex index, ReaderCohortIndex? cohorts)
    {
        var genres = new Dictionary<int, double>();
        var tags = new Dictionary<int, double>();
        var total = 0.0;
        var scale = cohorts is null ? 0 : Math.Log(1 + Math.Max(1, cohorts.CompletionP99));

        for (var row = 0; row < index.Count; row++)
        {
            var weight = cohorts is null
                ? PopularityWeight(index.PopularityAt(row), index.Count)
                : ReaderWeight(cohorts, index.IdAt(row), scale);
            total += weight;

            foreach (var genre in index.GenresAt(row))
            {
                genres[genre] = genres.GetValueOrDefault(genre) + weight;
            }

            foreach (var (id, _) in TagMath.Unpack(index.TagsAt(row)))
            {
                tags[id] = tags.GetValueOrDefault(id) + weight;
            }
        }

        return new CatalogueBaseline(
            index, genres, tags, total,
            cohorts is null ? "popularity" : "readers",
            cohorts?.GeneratedAt);
    }

    /// <summary>
    /// How much one catalogue row counts toward "what people read". The dump gives a popularity
    /// rank, not a readership, so this is a proxy and the UI has to say so.
    /// <para>
    /// A straight <c>1/rank</c> would hand the single most popular title more weight than thousands
    /// of others combined, which measures fame rather than taste. A bounded linear percentile keeps
    /// the ordering while flattening that spike; the floor keeps the long tail contributing
    /// something rather than being rounded out of the catalogue entirely.
    /// </para>
    /// Rows with no rank count at the midpoint. They are unranked, not unpopular.
    /// </summary>
    private static double PopularityWeight(int popularity, int count)
    {
        if (popularity == VectorIndex.Unknown || count <= 1)
        {
            return 0.15 + 0.85 * 0.5;
        }

        var percentile = 1.0 - (popularity - 1) / (double)(count - 1);
        return 0.15 + 0.85 * Math.Clamp(percentile, 0, 1);
    }

    /// <summary>
    /// The same shape as <see cref="PopularityWeight"/>, over a real count of readers who finished
    /// the series instead of over its popularity rank. This is what the proxy was standing in for.
    /// <para>
    /// Deliberately <em>not</em> the raw count: completions have exactly the spike
    /// <see cref="PopularityWeight"/>'s own reasoning argues against, so they are scaled through a
    /// log against the artifact's 99th percentile rather than its maximum, which is one megahit.
    /// </para>
    /// <para>
    /// <b>A row with no entry takes the floor, not the midpoint, and that is a real change.</b>
    /// Under the proxy an unranked row was <em>unknown</em>, so the midpoint was the honest guess.
    /// Under real counts a missing row means the fetched readers finished it zero times, which is
    /// knowledge rather than absence.
    /// </para>
    /// </summary>
    private static double ReaderWeight(ReaderCohortIndex cohorts, long mangaBakaId, double scale)
    {
        if (!cohorts.TryGetSlot(mangaBakaId, out var slot))
        {
            return 0.15;
        }

        var completions = cohorts.GlobalCompletionsAt(slot);
        return completions <= 0 || scale <= 0
            ? 0.15
            : 0.15 + (0.85 * Math.Clamp(Math.Log(1 + completions) / scale, 0, 1));
    }

    private sealed class Aggregated
    {
        public Dictionary<string, double> Genres { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> Creators { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> Types { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double> Years { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, int> GenreSupport { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> TagSupport { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> CreatorSupport { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> TypeSupport { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int SeriesCount { get; set; }
    }

    /// <summary>
    /// Catalogue shares, resolved by name on demand. Holds the index it was built against so a name
    /// can be turned into that index's ids; a name the vocabulary never saw simply has no share, and
    /// the facet goes out without a catalogue ratio.
    /// </summary>
    private sealed class CatalogueBaseline(
        VectorIndex index,
        Dictionary<int, double> genres,
        Dictionary<int, double> tags,
        double total,
        string source,
        DateTime? cohortsAt)
    {
        /// <summary>
        /// Which population the shares were weighted by: <c>readers</c> once the cohort artifact is
        /// installed, <c>popularity</c> while it is not. The UI says which, because "weighted toward
        /// titles more people read" is literally true of one and a stand-in for the other.
        /// </summary>
        public string Source { get; } = source;

        /// <summary>
        /// The cohort artifact this was built against, so installing a new one rebuilds rather than
        /// waiting out the day-long TTL.
        /// </summary>
        public DateTime? CohortsAt { get; } = cohortsAt;

        public IReadOnlyDictionary<string, double> GenreShare { get; } =
            new NameShares(name => index.TryGetGenreId(name, out var id) && genres.TryGetValue(id, out var w)
                ? w / total
                : null);

        // A name can be interned under several casing variants; a row carries one of them, so the
        // name's share is their sum.
        public IReadOnlyDictionary<string, double> TagShare { get; } =
            new NameShares(name =>
            {
                if (!index.TryGetTagIds(name, out var ids))
                {
                    return null;
                }

                var sum = ids.Sum(id => tags.GetValueOrDefault(id));
                return sum > 0 ? sum / total : null;
            });
    }

    /// <summary>
    /// A read-only lookup that resolves on access rather than materializing a share for every name
    /// in the catalogue. Only the handful of names actually in a profile are ever asked for.
    /// </summary>
    private sealed class NameShares(Func<string, double?> resolve) : IReadOnlyDictionary<string, double>
    {
        public bool TryGetValue(string key, out double value)
        {
            var resolved = resolve(key);
            value = resolved ?? 0;
            return resolved is not null;
        }

        public bool ContainsKey(string key) => resolve(key) is not null;

        public double this[string key] => resolve(key) ?? throw new KeyNotFoundException(key);

        public IEnumerable<string> Keys => throw new NotSupportedException("Resolved on access only.");

        public IEnumerable<double> Values => throw new NotSupportedException("Resolved on access only.");

        public int Count => throw new NotSupportedException("Resolved on access only.");

        public IEnumerator<KeyValuePair<string, double>> GetEnumerator() =>
            throw new NotSupportedException("Resolved on access only.");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
