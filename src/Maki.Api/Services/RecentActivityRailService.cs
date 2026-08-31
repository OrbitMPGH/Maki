using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
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
    /// </summary>
    private const int SeedCount = 8;

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
        var items = result.Related.Take(MaxRelated).Concat(result.Similar).Take(RailSize).ToList();
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
            .Select(g => new { SeriesId = g.Key, LastReadAt = g.Max(p => p.UpdatedAt) })
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
            .Select(s => new { s.Id, MangaBakaId = (long)s.MangaBakaId!.Value, s.Title })
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

            seeds.Add(new RecentSeed(series.MangaBakaId, series.Title));
            if (seeds.Count == SeedCount)
            {
                break;
            }
        }

        return seeds;
    }

    private record RecentSeed(long MangaBakaId, string Title);
}
