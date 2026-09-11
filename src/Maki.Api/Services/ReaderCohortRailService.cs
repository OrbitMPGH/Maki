using Maki.Core.Security;
using Maki.Data;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;
using Maki.Metadata.ReaderCohorts;

namespace Maki.Api.Services;

/// <summary>
/// "Readers like you also finished": what the reader's own cohorts read that they have not.
///
/// <para>
/// The second per-user rail on Discover, and unlike
/// <see cref="RecentActivityRailService"/> it does not delegate its ranking. That rail narrows the
/// recommender's seeds and lets it do everything else; this one answers a question the recommender
/// cannot ask, since "which group of readers does this person belong with" is not a similarity in
/// the vector space. What it does reuse is every seam that decides <em>which</em> series may be
/// shown at all: the owned-series exclusion, the caller's content-rating ceiling, the vector
/// index's filter plan, and <see cref="MangaBakaLocalStore.GetByIdsAsync"/> for hydration.
/// </para>
///
/// <para>
/// <b>Ranked by damped lift, not by how much a cohort read something.</b> A title most people
/// finish has a high completion rate in every cohort, so the raw rate returns the same famous list
/// to everybody: measured, median popularity rank 183 of 128,116. Dividing the overall rate back
/// out fixes that and overshoots if taken all the way — pure lift returns titles almost nobody goes
/// on to finish (recall 0.005). <see cref="ReaderCohortTuning.PopularityDamping"/> is the dial and
/// carries the sweep.
/// </para>
/// </summary>
public class ReaderCohortRailService(
    IServiceScopeFactory scopeFactory,
    SeedWeightService seedWeights,
    ReaderCohortService cohorts,
    VectorIndexCache vectorIndex,
    MangaBakaLocalStore store,
    ILogger<ReaderCohortRailService> logger)
{
    /// <summary>
    /// Deliberately not a <c>BrowseFeed</c> name. <c>DiscoverService.GetFeedAsync</c> rejects it,
    /// which is what stops the "Show more" view paging this rail as though it were a catalogue
    /// browse — it has its own endpoint, because its ordering exists nowhere else.
    /// </summary>
    public const string RailFeed = "ReaderCohorts";

    public const string RailKey = "reader-cohorts";

    private const int RailSize = 40;

    /// <summary>
    /// The rail, or null when there is nothing to show: the artifact is absent or off, the reader
    /// has no library, or they finished too little for any cohort to claim them. Null rather than
    /// an empty rail, so the client leaves the row out entirely instead of rendering a heading over
    /// nothing.
    /// </summary>
    public async Task<DiscoverRail?> GetAsync(
        ICurrentUser scope, RecommendationFilters? filters, int limit, CancellationToken ct = default)
    {
        IReadOnlySet<long> owned;
        using (var dbScope = scopeFactory.CreateScope())
        {
            var db = dbScope.ServiceProvider.GetRequiredService<MakiDbContext>();
            // A singleton opening its own scope gets an unrestricted DataScope, which would exclude
            // series from root folders the caller cannot see and leak their existence by omission.
            db.Scope.SetUser(scope.UserId, scope.AllRootFolders);
            owned = (await seedWeights.BuildAsync(db, scope, ct)).LibraryIds.ToHashSet();
        }

        if (owned.Count == 0)
        {
            return null;
        }

        var allowed = ContentRating.Allowed(scope.MaxContentRating);
        var accept = await BuildFilterAsync(filters, ct);

        var ids = await cohorts.GetCandidatesAsync(scope, owned, accept, limit, ct);
        if (ids.Count == 0)
        {
            return null;
        }

        var items = await store.GetByIdsAsync([.. ids], allowed, ct);
        if (items.Count == 0)
        {
            return null;
        }

        logger.LogDebug(
            "Reader-cohort rail for user {User}: {Ids} candidates, {Items} hydrated",
            scope.UserId, ids.Count, items.Count);

        return new DiscoverRail(
            RailKey,
            "Readers like you also finished",
            RailFeed,
            Genre: null,
            Items: items,
            Subtitle: "From readers whose finished series look like yours.");
    }

    public Task<DiscoverRail?> GetAsync(ICurrentUser scope, CancellationToken ct = default) =>
        GetAsync(scope, filters: null, RailSize, ct);

    /// <summary>
    /// Turns the caller's filters into a per-candidate predicate through the vector index's own
    /// filter plan, so a genre or year narrows the ranking rather than deleting rows out of a page
    /// that was already cut. Duplicating <c>RecommendationFilters</c>' logic here would be a third
    /// copy to keep in step and a way to smuggle rows past a filter.
    /// <para>
    /// Null when there is nothing to apply, or when the index is not built — in which case the
    /// content-rating ceiling still runs, inside
    /// <see cref="MangaBakaLocalStore.GetByIdsAsync"/>, because a ceiling that silently stops
    /// applying when an unrelated index is missing is the kind of hole nobody notices.
    /// </para>
    /// </summary>
    private async Task<Func<long, bool>?> BuildFilterAsync(
        RecommendationFilters? filters, CancellationToken ct)
    {
        if (filters is null || filters == RecommendationFilters.None)
        {
            return null;
        }

        var index = await vectorIndex.GetAsync(ct);
        if (index is null || index.Count == 0)
        {
            return null;
        }

        var plan = index.Plan(filters);
        if (plan.Impossible)
        {
            return _ => false;
        }

        return id => index.TryGetRow(id, out var row) && index.Matches(row, plan);
    }
}
