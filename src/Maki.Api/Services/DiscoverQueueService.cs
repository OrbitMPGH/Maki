using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Recommendations;
using Maki.Core.Security;
using Maki.Data;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public static class QueueOptions
{
    public const int DeckSize = 12;
    public const int MaxTake = 40;
    public const int MaxExclude = MaxTake * 20;
    public const int TrendingSlots = 2;
    public const int FranchiseCap = 1;

    /// <summary>Zero-based deck positions trending cards are dealt into: the 5th and the 10th.</summary>
    public static readonly int[] TrendingPositions = [4, 9];

    public const string TasteOrigin = "taste";
    public const string TrendingOrigin = "trending";
}

public record QueueRequest(int Take = QueueOptions.DeckSize, IReadOnlyList<long>? Exclude = null, bool Refresh = false);

/// <summary>A recommendation on the wire exactly as the rails send it, plus where it came from.</summary>
public sealed record QueueCard : MangaBakaRecommendation
{
    public QueueCard(MangaBakaRecommendation pick, string origin, long feedbackRevision) : base(pick)
    {
        Origin = origin;
        FeedbackRevision = feedbackRevision;
    }

    public string Origin { get; init; }

    /// <summary>
    /// The title's current <c>RecommendationFeedback.Revision</c>, 0 when it has no row. What
    /// <c>PUT feedback/{id}</c> needs as <c>expectedRevision</c>, so a swipe needs no state fetch first.
    /// </summary>
    public long FeedbackRevision { get; init; }
}

public record QueueDeck(IReadOnlyList<QueueCard> Cards, DateTime GeneratedAt, bool ColdStart, bool Exhausted);

/// <summary>
/// Deals the Discovery Queue: the Recommended tab's own pool with the trending rail mixed in, minus
/// everything the reader already owns, saved, requested or answered. Nothing merged is cached: the
/// pool and the trending rail are cached where they live, and the exclusions are per call.
/// </summary>
public class DiscoverQueueService(
    MakiDbContext db,
    ICurrentUser currentUser,
    RecommendationService recommendations,
    DiscoverService discover,
    HiddenContentService hidden,
    IUserSettings userSettings,
    SemanticRecommender? semantic = null)
{
    public async Task<QueueDeck> DealAsync(QueueRequest request, CancellationToken ct = default)
    {
        var taste = await recommendations.VisibleSimilarAsync(await PoolRequestAsync(request.Refresh, ct), currentUser, ct);
        var trending = await TrendingAsync(ct);
        return await ComposeAsync(request, taste, trending, ct);
    }

    /// <summary>
    /// The caller's saved Recommended defaults as a request, built the way the client builds it so
    /// the pool key matches the Recommended tab's and the two share one pool. Saved seeds are left
    /// out on purpose: the queue is always about the whole library.
    /// </summary>
    private async Task<RecommendationRequest> PoolRequestAsync(bool refresh, CancellationToken ct)
    {
        var spec = RecommendationDefaultsSpec.Parse(await userSettings.GetAsync(SettingKeys.RecommendationsDefaults, ct));
        static IReadOnlyList<T>? NonEmpty<T>(IReadOnlyList<T>? values) => values is { Count: > 0 } ? values : null;
        var filters = new RecommendationFilters(
            spec.YearMin, spec.YearMax, NonEmpty(spec.Types), NonEmpty(spec.Statuses), spec.MinRating,
            NonEmpty(spec.Genres), spec.MinChapters, spec.MaxChapters, NonEmpty(spec.Tags),
            NonEmpty(spec.ContentRatings), NonEmpty(spec.Rules));
        return new RecommendationRequest(
            Filters: await hidden.ApplyAsync(HiddenContentService.Sanitize(filters), ct),
            Obscurity: spec.Obscurity,
            Diversity: spec.Diversity,
            Refresh: refresh);
    }

    private async Task<IReadOnlyList<MangaBakaRecommendation>> TrendingAsync(CancellationToken ct)
    {
        var rails = await discover.GetFeedsAsync(false, currentUser.MaxContentRating, ct, DiscoverService.RefillRailSize);
        var items = rails.FirstOrDefault(r => r.Feed == nameof(BrowseFeed.Trending))?.Items ?? [];
        return HiddenContentService.Without(items, await hidden.PredicateAsync(ct));
    }

    /// <param name="taste">The visible similar pool, or null when the reader has no seeds at all.</param>
    internal async Task<QueueDeck> ComposeAsync(QueueRequest request,
        IReadOnlyList<MangaBakaRecommendation>? taste, IReadOnlyList<MangaBakaRecommendation> trending,
        CancellationToken ct = default)
    {
        var take = Math.Clamp(request.Take, 1, QueueOptions.MaxTake);
        var excluded = (request.Exclude ?? []).Take(QueueOptions.MaxExclude).ToHashSet();
        var candidates = (taste ?? []).Concat(trending).Select(Id).Where(id => id > 0 && !excluded.Contains(id))
            .ToHashSet();
        excluded.UnionWith(await ExclusionsAsync(db, currentUser.UserId, candidates, DateTime.UtcNow, ct));

        var tasteLeft = Remaining(taste ?? [], excluded);
        var tasteIds = tasteLeft.Select(Id).ToHashSet();
        var trendingLeft = await WithFranchisesAsync(
            Remaining(trending, excluded).Where(p => !tasteIds.Contains(Id(p))).Take(QueueOptions.MaxTake * 2).ToList(), ct);

        var dealt = Deal(tasteLeft, trendingLeft, take);
        var dealtIds = dealt.Select(x => Id(x.Pick)).ToHashSet();
        var ids = dealt.Select(x => Id(x.Pick)).ToList();
        var revisions = await db.RecommendationFeedback.AsNoTracking()
            .Where(x => x.UserId == currentUser.UserId && x.Provider == "mangabaka" && ids.Contains(x.ProviderId))
            .ToDictionaryAsync(x => x.ProviderId, x => x.Revision, ct);

        return new QueueDeck(
            dealt.Select(x => new QueueCard(x.Pick, x.Origin, revisions.GetValueOrDefault(Id(x.Pick)))).ToList(),
            DateTime.UtcNow,
            ColdStart: taste is null,
            // Measured after the deal, so the deck that empties the sources already says so.
            Exhausted: tasteLeft.Concat(trendingLeft).All(p => dealtIds.Contains(Id(p))));
    }

    /// <summary>
    /// The <paramref name="candidates"/> the queue must never deal to this reader: owned, saved,
    /// pending a request, or already answered through feedback (an active suppression, or any
    /// thumbs either way).
    /// </summary>
    internal static async Task<HashSet<long>> ExclusionsAsync(MakiDbContext db, int userId,
        IReadOnlyCollection<long> candidates, DateTime now, CancellationToken ct = default)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var ids = candidates.ToList();
        var seriesIds = ids.Where(id => id <= int.MaxValue).Select(id => (int)id).ToList();
        var excluded = (await db.Series.AsNoTracking()
            .Where(s => s.MangaBakaId != null && seriesIds.Contains(s.MangaBakaId.Value))
            .Select(s => (long)s.MangaBakaId!.Value).ToListAsync(ct)).ToHashSet();

        var feedback = await db.RecommendationFeedback.AsNoTracking()
            .Where(x => x.UserId == userId && ids.Contains(x.ProviderId))
            .Select(x => new RecommendationFeedback
            {
                ProviderId = x.ProviderId, Suppression = x.Suppression, Sentiment = x.Sentiment,
                DismissedUntilUtc = x.DismissedUntilUtc, Exposure = x.Exposure,
            })
            .ToListAsync(ct);
        excluded.UnionWith(feedback
            .Where(x => x.Sentiment != RecommendationSentiment.None || RecommendationFeedbackPolicy.Suppresses(x, now))
            .Select(x => x.ProviderId));

        excluded.UnionWith(await db.PlanToReadEntries.AsNoTracking()
            .Where(x => x.UserId == userId && ids.Contains(x.ProviderId)).Select(x => x.ProviderId).ToListAsync(ct));

        // Joined on the caller explicitly: the requests filter lets an admin read everybody's.
        var providerIds = ids.Select(id => id.ToString()).ToList();
        var requested = await db.SeriesRequests.AsNoTracking()
            .Where(r => r.UserId == userId && r.Kind == SeriesRequestKind.NewSeries &&
                (r.Status == SeriesRequestStatus.Pending || r.Status == SeriesRequestStatus.Processing) &&
                r.MetadataProviderId != null && providerIds.Contains(r.MetadataProviderId))
            .Select(r => r.MetadataProviderId!).ToListAsync(ct);
        excluded.UnionWith(requested.Select(long.Parse));
        return excluded;
    }

    /// <summary>
    /// Taste cards in pool order with trending cards at <see cref="QueueOptions.TrendingPositions"/>,
    /// at most <see cref="QueueOptions.FranchiseCap"/> per franchise, either list filling in when the
    /// other runs out.
    /// </summary>
    internal static IReadOnlyList<(MangaBakaRecommendation Pick, string Origin)> Deal(
        IReadOnlyList<MangaBakaRecommendation> taste, IReadOnlyList<MangaBakaRecommendation> trending, int take)
    {
        var tasteQueue = new LinkedList<MangaBakaRecommendation>(taste);
        var trendingQueue = new LinkedList<MangaBakaRecommendation>(trending);
        var franchises = new Dictionary<int, int>();
        var seen = new HashSet<long>();
        var dealt = new List<(MangaBakaRecommendation, string)>(take);

        MangaBakaRecommendation? Next(LinkedList<MangaBakaRecommendation> queue)
        {
            while (queue.First is { } node)
            {
                queue.RemoveFirst();
                var pick = node.Value;
                if (!seen.Add(Id(pick)))
                {
                    continue;
                }

                if (pick.FranchiseId is int franchise)
                {
                    if (franchises.GetValueOrDefault(franchise) >= QueueOptions.FranchiseCap)
                    {
                        continue;
                    }

                    franchises[franchise] = franchises.GetValueOrDefault(franchise) + 1;
                }

                return pick;
            }

            return null;
        }

        var trendingDealt = 0;
        while (dealt.Count < take && (tasteQueue.Count > 0 || trendingQueue.Count > 0))
        {
            var wantTrending = trendingDealt < QueueOptions.TrendingSlots &&
                QueueOptions.TrendingPositions.Contains(dealt.Count);
            var (first, firstOrigin, second, secondOrigin) = wantTrending
                ? (trendingQueue, QueueOptions.TrendingOrigin, tasteQueue, QueueOptions.TasteOrigin)
                : (tasteQueue, QueueOptions.TasteOrigin, trendingQueue, QueueOptions.TrendingOrigin);
            if (Next(first) is { } pick)
            {
                dealt.Add((pick, firstOrigin));
            }
            else if (Next(second) is { } fallback)
            {
                dealt.Add((fallback, secondOrigin));
            }
            else
            {
                break;
            }

            if (dealt[^1].Item2 == QueueOptions.TrendingOrigin)
            {
                trendingDealt++;
            }
        }

        return dealt;
    }

    private async Task<IReadOnlyList<MangaBakaRecommendation>> WithFranchisesAsync(
        IReadOnlyList<MangaBakaRecommendation> picks, CancellationToken ct)
    {
        if (semantic is null || picks.Count == 0 || !semantic.IsReady())
        {
            return picks;
        }

        var franchises = await semantic.FranchisesAsync(picks.Select(Id).ToList(), ct);
        return picks.Select(p => p.FranchiseId is null && franchises.TryGetValue(Id(p), out var f)
            ? p with { FranchiseId = f }
            : p).ToList();
    }

    private static List<MangaBakaRecommendation> Remaining(
        IReadOnlyList<MangaBakaRecommendation> picks, HashSet<long> excluded) =>
        picks.Where(p => Id(p) is var id && id > 0 && !excluded.Contains(id)).ToList();

    private static long Id(MangaBakaRecommendation pick) =>
        long.TryParse(pick.ProviderId, out var id) ? id : 0;
}
