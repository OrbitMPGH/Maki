using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// The Discover page's one personalised rail: recommendations seeded from the handful of series the
/// caller read most recently, rather than from their whole library.
///
/// <para>
/// It is a thin front end for <see cref="RecommendationService"/> — the seeds are narrowed, nothing
/// else is. That is deliberate: scoring, the owned-series exclusion, the content-rating clamp, the
/// behavioural seed weights and the 12-hour pool cache all already live there, and a second scan
/// with its own copy of those rules is exactly how the two would drift apart. It also means the rail
/// costs nothing on a warm pool, since <see cref="RecommendationService"/> keys its cache on the
/// seed set and the rail asks for the same seeds every time until the caller reads something new.
/// </para>
///
/// <para>
/// Distinct from the whole-library picks on the Recommended tab, and worth having alongside them: a
/// library accumulates over years and its centroid barely moves, so those picks are stable to the
/// point of being static. Seeding on the last few things somebody actually read is what makes the
/// rail respond to what they are into this week.
/// </para>
/// </summary>
public class RecentActivityRailService(
    IServiceScopeFactory scopeFactory,
    RecommendationService recommendations,
    ILogger<RecentActivityRailService> logger)
{
    public const string RailKey = "recent-activity";

    /// <summary>
    /// The rail's <see cref="DiscoverRail.Feed"/>. Deliberately not a <c>BrowseFeed</c>
    /// name — this rail has no catalogue-browse ordering to re-query, and
    /// <see cref="DiscoverService.GetFeedAsync"/> rejects it. The client branches on
    /// <see cref="DiscoverRail.SeedIds"/> instead, so the value here is only ever a label.
    /// </summary>
    public const string RailFeed = "RecentActivity";

    /// <summary>
    /// How many recently-read series seed the rail. Small on purpose: the point is to answer "what
    /// have you been reading lately", and a seed set large enough to average out is the whole-library
    /// behaviour the Recommended tab already provides.
    /// <para>
    /// Six rather than a rounder number because <see cref="GetGroupedAsync"/> renders one card per
    /// seed in a three-column grid, and six fills two rows exactly. Eight left two cards stranded on
    /// a half-empty third row.
    /// </para>
    /// </summary>
    private const int SeedCount = 6;

    /// <summary>
    /// The rail's length. Matches <c>DiscoverService.RailSize</c> and, not coincidentally,
    /// <c>RecommendationService.PageSize</c> — page 0 of the pool fills the rail exactly.
    /// </summary>
    private const int RailSize = 40;

    /// <summary>
    /// How many direct relations (sequels, spin-offs) of the seeds may lead the rail. Capped because
    /// finishing one long-running series can produce a dozen of them, and a rail that is nothing but
    /// one franchise's side stories isn't discovery.
    /// </summary>
    private const int MaxRelated = 6;

    /// <summary>
    /// How many of those <see cref="MaxRelated"/> slots any one franchise may hold.
    ///
    /// <para>
    /// The cap alone was not enough. Relations come back rated best first across every seed at once,
    /// so one seed with a lot of well-rated side stories takes the whole block: the reported case was
    /// four Nagatoro spin-offs sitting at positions three to six of a rail seeded by six different
    /// series. Two is what a sequel and one side story need, and it leaves four slots for the other
    /// five seeds.
    /// </para>
    ///
    /// <para>
    /// The surplus is dropped from the rail rather than pushed down it, since the relations that do
    /// not lead are not in the similar pool either (<c>RecommendationService</c> excludes them from
    /// the scan). They are still one tap away: the expanded view pages the recommender on the rail's
    /// own <see cref="DiscoverRail.SeedIds"/> and gets the full relation list back.
    /// </para>
    /// </summary>
    private const int MaxRelatedPerFranchise = 2;

    /// <summary>
    /// How many pages of the cached pool <see cref="GetGroupedAsync"/> will draw on to fill its
    /// cards. One page of 40 reached four of six seeds on the simulated library; the pool is
    /// already built by then, so the extra pages are slices rather than scans.
    /// </summary>
    private const int MaxGroupedPages = 4;

    /// <returns>
    /// The rail, or null when there is nothing to build it from: an unauthenticated caller, nobody
    /// who has finished a chapter yet, or no recently-read series that carries a MangaBaka id. Null
    /// rather than an empty rail so the client can leave the row out entirely instead of rendering a
    /// heading over nothing.
    /// </returns>
    public async Task<DiscoverRail?> GetAsync(
        ICurrentUser scope, bool refresh, CancellationToken ct = default)
    {
        var seeds = await RecentSeedsAsync(scope, ct);
        if (seeds.Count == 0)
        {
            return null;
        }

        var seedIds = seeds.Select(s => s.MangaBakaId).ToList();
        var result = await recommendations.GetAsync(
            new RecommendationRequest(SeedIds: seedIds, Refresh: refresh), scope, ct);

        // Relations first: a sequel to something finished last week is the most actionable pick on
        // the rail, and there are rarely many. Similar picks fill the rest. The two sets cannot
        // overlap — RecommendationService excludes everything it returned as related from the
        // similarity scan — so this needs no dedupe.
        var items = LeadingRelations(result.Related).Concat(result.Similar).Take(RailSize).ToList();
        if (items.Count == 0)
        {
            return null;
        }

        logger.LogDebug(
            "Recent-activity rail for user {UserId}: {Seeds} seed(s), {Items} item(s)",
            scope.UserId, seeds.Count, items.Count);

        return new DiscoverRail(
            RailKey,
            "Based on your recent activity",
            RailFeed,
            Genre: null,
            items,
            Subtitle: Because(seeds),
            SeedIds: seedIds);
    }

    /// <summary>
    /// The relations that lead the rail: the best-rated <see cref="MaxRelated"/> of them, no more
    /// than <see cref="MaxRelatedPerFranchise"/> from any one franchise.
    ///
    /// <para>
    /// Franchise first because two seeds can sit in the same one (somebody who read Nagatoro and one
    /// of its spin-offs seeds both), and their relations are then the same pile of side stories under
    /// two different headings. It falls back to the seed a relation hangs off when the vector index
    /// has no component for it, and to the pick's own id when there is no seed title either, which
    /// means "cannot tell": an unknown franchise must not group with another unknown one.
    /// </para>
    /// </summary>
    private static List<MangaBakaRecommendation> LeadingRelations(
        IReadOnlyList<MangaBakaRecommendation> related)
    {
        var taken = new Dictionary<string, int>();
        var lead = new List<MangaBakaRecommendation>(MaxRelated);
        foreach (var pick in related)
        {
            var key = pick switch
            {
                { FranchiseId: int franchise } => $"f{franchise}",
                { RelatedToTitle: { Length: > 0 } seed } => $"s{seed}",
                _ => $"p{pick.ProviderId}",
            };
            var count = taken.GetValueOrDefault(key);
            if (count >= MaxRelatedPerFranchise)
            {
                continue;
            }

            taken[key] = count + 1;
            lead.Add(pick);
            if (lead.Count == MaxRelated)
            {
                break;
            }
        }

        return lead;
    }

    /// <summary>
    /// The same picks as <see cref="GetAsync"/>, split into one rail per seed instead of one rail
    /// for all of them.
    ///
    /// <para>
    /// It is deliberately the <em>same single</em> recommender call. Asking once per seed would be
    /// the obvious shape and is the wrong one: <c>RecommendationService</c> caches its candidate
    /// pool keyed on the seed set, so six single-seed requests are six pool builds and six cache
    /// entries that share nothing, on a page that already asks for four other things.
    /// </para>
    ///
    /// <para>
    /// Six separate calls would also thrash the cache they cannot share:
    /// <c>RecommendationService</c> keeps sixteen pool slots for the whole instance behind one
    /// semaphore, so six per-user pools both serialise and evict everybody else's.
    /// </para>
    ///
    /// <para>
    /// <see cref="SeedFor"/> does the splitting. A pick that overlaps no seed at all is dropped
    /// rather than swept into an "other" bucket, because a card headed by a series you read is the
    /// whole claim this rail makes.
    /// </para>
    /// </summary>
    /// <returns>
    /// One rail per seed that attracted at least one pick, most recently read first. Empty when the
    /// caller cannot be seeded at all, for the same reasons <see cref="GetAsync"/> answers null.
    /// </returns>
    public async Task<IReadOnlyList<DiscoverRail>> GetGroupedAsync(
        ICurrentUser scope, bool refresh, CancellationToken ct = default)
    {
        var seeds = await RecentSeedsAsync(scope, ct);
        if (seeds.Count == 0)
        {
            return [];
        }

        var seedIds = seeds.Select(s => s.MangaBakaId).ToList();

        // Relations first for the same reason the flat rail leads with them: a sequel to something
        // read last week is the most actionable pick there is.
        var byTitle = seeds.ToDictionary(s => s.Title, _ => new List<MangaBakaRecommendation>(),
            StringComparer.OrdinalIgnoreCase);

        // Several pages, because one is not enough supply to reach six cards. A page is a slice of
        // a pool that is already computed and cached, so pages past the first cost a dictionary
        // lookup rather than a scan — the expensive part ran on page 0. Stops early once every seed
        // has something, which on a varied library is usually the first page.
        for (var page = 0; page < MaxGroupedPages; page++)
        {
            var result = await recommendations.GetAsync(
                new RecommendationRequest(SeedIds: seedIds, Page: page, Refresh: refresh && page == 0),
                scope, ct);

            foreach (var item in result.Related.Concat(result.Similar))
            {
                var seed = SeedFor(item, seeds, byTitle);
                if (seed is not null)
                {
                    byTitle[seed.Title].Add(item);
                }
            }

            if (!result.HasMore || byTitle.Values.All(v => v.Count > 0))
            {
                break;
            }
        }

        var rails = new List<DiscoverRail>(seeds.Count);
        foreach (var seed in seeds)
        {
            var items = byTitle[seed.Title];
            if (items.Count == 0)
            {
                continue;
            }

            rails.Add(new DiscoverRail(
                $"{RailKey}-{seed.MangaBakaId}",
                $"Because you read {seed.Title}",
                RailFeed,
                Genre: null,
                items,
                Subtitle: null,
                SeedIds: [seed.MangaBakaId],
                Seed: new SeedState(seed.Title, seed.ChaptersRead, seed.ChaptersAvailable, seed.State)));
        }

        logger.LogDebug(
            "Grouped recent-activity rails for user {UserId}: {Seeds} seed(s), {Rails} rail(s)",
            scope.UserId, seeds.Count, rails.Count);

        return rails;
    }

    /// <summary>
    /// Which seed a pick belongs under.
    ///
    /// <para>
    /// The recommender's own answer comes first: <see cref="MangaBakaRecommendation.RelatedToTitle"/>
    /// for a relation, then <see cref="MangaBakaRecommendation.BecauseOfTitle"/> for the seed whose
    /// feel most drove a semantic pick. Both are exact and both are preferred.
    /// </para>
    ///
    /// <para>
    /// Neither is usually available, which is the whole reason this method exists.
    /// <c>BecauseOfTitle</c> is null for a genre-only hit, and on a library whose seeds share a
    /// genre that is most of them: measured against the simulated library, 200 similar picks over
    /// six seeds carried an attributed title on twelve, spread across two seeds. Grouping on it
    /// alone left four of six cards empty.
    /// </para>
    ///
    /// <para>
    /// So the rest are attributed on overlap instead, against the tags and genres the pick was
    /// matched on. Tags outweigh genres because a genre is shared by a third of the catalogue and
    /// says almost nothing about which of six seeds a pick belongs to. A tie goes to the seed with
    /// fewer picks so far, which keeps one strong seed from taking the row; ties beyond that go to
    /// the more recently read seed, since <paramref name="seeds"/> is already in that order.
    /// </para>
    /// </summary>
    /// <returns>The seed to file this pick under, or null when it overlaps none of them at all.</returns>
    private static RecentSeed? SeedFor(
        MangaBakaRecommendation item,
        IReadOnlyList<RecentSeed> seeds,
        IReadOnlyDictionary<string, List<MangaBakaRecommendation>> assigned)
    {
        var named = item.RelatedToTitle ?? item.BecauseOfTitle;
        if (named is not null)
        {
            var exact = seeds.FirstOrDefault(s => string.Equals(s.Title, named, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        RecentSeed? best = null;
        var bestScore = 0;
        var bestLoad = int.MaxValue;
        foreach (var seed in seeds)
        {
            var score = (Overlap(item.MatchedTags, seed.Tags) * TagWeight)
                        + Overlap(item.MatchedGenres, seed.Genres);
            if (score == 0)
            {
                continue;
            }

            var load = assigned[seed.Title].Count;
            if (score > bestScore || (score == bestScore && load < bestLoad))
            {
                best = seed;
                bestScore = score;
                bestLoad = load;
            }
        }

        return best;
    }

    /// <summary>A tag match is worth this many genre matches when attributing a pick to a seed.</summary>
    private const int TagWeight = 3;

    private static int Overlap(IReadOnlyList<string> a, IReadOnlyList<string> b) =>
        a.Count == 0 || b.Count == 0
            ? 0
            : a.Count(x => b.Contains(x, StringComparer.OrdinalIgnoreCase));

    /// <summary>"Because you read A, B and C" — the seeds, most recent first, at most three named.</summary>
    private static string Because(IReadOnlyList<RecentSeed> seeds)
    {
        var named = seeds.Take(3).Select(s => s.Title).ToList();
        var list = named.Count switch
        {
            1 => named[0],
            2 => $"{named[0]} and {named[1]}",
            _ => $"{named[0]}, {named[1]} and {named[2]}",
        };
        return seeds.Count > named.Count
            ? $"Because you read {list} and {seeds.Count - named.Count} more"
            : $"Because you read {list}";
    }

    /// <summary>
    /// The caller's most recently read series that can seed the recommender, newest first.
    ///
    /// <para>
    /// "Read" is <see cref="ReadCounts.ReadFor"/> — a completed chapter that is actually downloaded —
    /// so the rail agrees with every other read count in the app rather than inventing a second
    /// definition. That query runs with the global filters off and a named user, so visibility is put
    /// back by resolving the series through the *scoped* <c>db.Series</c> below: a series in a root
    /// folder the caller can no longer see must neither seed the rail nor be named in its subtitle.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<RecentSeed>> RecentSeedsAsync(
        ICurrentUser scope, CancellationToken ct)
    {
        if (scope.UserId <= 0)
        {
            return [];
        }

        using var dbScope = scopeFactory.CreateScope();
        var db = dbScope.ServiceProvider.GetRequiredService<MakiDbContext>();
        db.Scope.SetUser(scope.UserId, scope.AllRootFolders);

        // Over-fetch: the rows dropped below (no MangaBaka id, fully incognito, out of scope) are
        // only knowable after the join, and taking exactly SeedCount here would hand back fewer
        // seeds than asked for whenever any of them applies.
        var recent = await ReadCounts.ReadFor(db, scope.UserId)
            .GroupBy(p => p.SeriesId)
            .Select(g => new
            {
                SeriesId = g.Key,
                LastReadAt = g.Max(p => p.UpdatedAt),
                // Counted here rather than re-queried per seed: the grouping is already running.
                Read = g.Count(),
            })
            .OrderByDescending(x => x.LastReadAt)
            .Take(SeedCount * 4)
            .ToListAsync(ct);

        if (recent.Count == 0)
        {
            return [];
        }

        var candidates = recent.Select(r => r.SeriesId).ToList();
        // Fully-incognito series are excluded for the same reason BehavioralTasteService excludes
        // them, only more visibly: their ChapterProgress rows exist, and a rail that says "because
        // you read X" would put a title the user asked to leave no trace of on the front page.
        // ScrobbleOnly is kept — it already counts in Rewind and read history.
        var visible = await db.Series
            .Where(s => candidates.Contains(s.Id)
                        && s.MangaBakaId != null
                        && s.Incognito != IncognitoMode.Full)
            .Select(s => new
            {
                s.Id,
                MangaBakaId = (long)s.MangaBakaId!.Value,
                s.Title,
                s.Status,
                s.Genres,
                s.Tags,
                // The denominator the reader can actually reach. Downloaded chapters, not the
                // provider's chapter count: "ch 40 of 200" when only 41 exist on disk reads as
                // barely started when they are in fact caught up.
                Available = db.Chapters.Count(c => c.SeriesId == s.Id && c.ChapterFileId != null),
            })
            .ToListAsync(ct);
        var byId = visible.ToDictionary(s => s.Id);

        var seeds = new List<RecentSeed>(SeedCount);
        var seen = new HashSet<long>();
        foreach (var row in recent.OrderByDescending(r => r.LastReadAt))
        {
            if (!byId.TryGetValue(row.SeriesId, out var series))
            {
                continue;
            }

            // MangaBakaId carries no unique index, so two local series can map to one catalogue
            // entry; keep the more recently read of the pair rather than seeding it twice.
            if (!seen.Add(series.MangaBakaId))
            {
                continue;
            }

            seeds.Add(new RecentSeed(
                series.MangaBakaId,
                series.Title,
                row.Read,
                series.Available,
                StateOf(row.Read, series.Available, series.Status),
                [.. series.Genres],
                [.. series.Tags]));
            if (seeds.Count == SeedCount)
            {
                break;
            }
        }

        return seeds;
    }

    /// <summary>
    /// Which of the three states a seed is in. "Caught up" is the one worth separating: a long
    /// weekly is never finished, but a reader with nothing left to read of it is in a different
    /// position from one who stopped halfway, and the card says so.
    /// </summary>
    private static string StateOf(int read, int available, SeriesStatus status)
    {
        if (available <= 0 || read < available)
        {
            return "reading";
        }

        return status == SeriesStatus.Completed ? "finished" : "caught-up";
    }

    private record RecentSeed(
        long MangaBakaId, string Title, int ChaptersRead, int ChaptersAvailable, string State,
        IReadOnlyList<string> Genres, IReadOnlyList<string> Tags);
}
