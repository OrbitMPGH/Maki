using Maki.Core.Configuration;
using Maki.Core.Recommendations;
using Maki.Metadata.Catalogue;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;

namespace Maki.Api.Services;

/// <summary>
/// The caller's never-show list (<see cref="HiddenContentSpec"/>), applied two ways: folded into a
/// request's filters so it narrows before a page is cut, and as a predicate over the shared rails,
/// which are cached instance-wide and so cannot carry one reader's list into their build. Also where
/// a request's creator filter is resolved, since every filtered request already passes through here.
/// </summary>
public class HiddenContentService(
    IUserSettings userSettings, VectorIndexCache vectorIndex, CatalogueIndexCache catalogueIndex,
    MangaBakaLocalStore store)
{
    private IReadOnlyList<CatalogueTerm>? _terms;
    private bool _loaded;

    public async Task<IReadOnlyList<CatalogueTerm>?> TermsAsync(CancellationToken ct = default)
    {
        if (!_loaded)
        {
            _terms = HiddenContentSpec.Parse(await userSettings.GetAsync(SettingKeys.DiscoverHidden, ct)).Terms;
            _loaded = true;
        }

        return _terms;
    }

    /// <summary>
    /// The filters with the caller's list in <see cref="RecommendationFilters.Hidden"/> and their
    /// creators resolved. Always overwrites: the list is a setting, and a request cannot choose to
    /// see past it.
    /// </summary>
    public async Task<RecommendationFilters> ApplyAsync(RecommendationFilters filters, CancellationToken ct = default) =>
        await ResolveCreditsAsync(filters with { Hidden = await TermsAsync(ct) }, ct);

    /// <summary>Fills <see cref="RecommendationFilters.CreditIds"/> from the filter's named creators.</summary>
    public async Task<RecommendationFilters> ResolveCreditsAsync(RecommendationFilters filters, CancellationToken ct = default)
    {
        if (filters.Credits is not { Count: > 0 })
        {
            return CreditFilter.Resolve(filters, null, 0);
        }

        var catalogue = await catalogueIndex.GetAsync(ct);
        return CreditFilter.Resolve(filters, catalogue?.Credits, CatalogueOptions.Default.CreditResolveMaxDistance);
    }

    /// <summary>
    /// A request's filters bounded by the caller: rules trimmed to sane sizes, content ratings
    /// clamped to their ceiling, and their never-show list attached.
    /// </summary>
    public async Task<RecommendationFilters> ScopeAsync(
        RecommendationFilters? filters, string? maxContentRating, CancellationToken ct = default)
    {
        var scoped = Sanitize(filters) with
        {
            ContentRatings = filters?.ContentRatings is { Count: > 0 } requested
                ? ContentRating.Clamp(requested, maxContentRating)
                : ContentRating.Allowed(maxContentRating)
        };
        return await ApplyAsync(scoped, ct);
    }

    /// <summary>Normalizes client-supplied rules and drops any never-show list a request carried.</summary>
    public static RecommendationFilters Sanitize(RecommendationFilters? filters) =>
        (filters ?? RecommendationFilters.None) with
        {
            Rules = CatalogueRules.Normalize(filters?.Rules),
            Hidden = null,
            Credits = CatalogueCredits.Normalize(filters?.Credits),
            CreditIds = null,
        };

    /// <summary>Whether the caller has a never-show list at all, without testing anything against it.</summary>
    public async Task<bool> HasTermsAsync(CancellationToken ct = default) =>
        await TermsAsync(ct) is { Count: > 0 };

    /// <summary>
    /// True for a MangaBaka id among <paramref name="candidates"/> the caller has hidden. Null when
    /// nothing is hidden.
    ///
    /// <para>
    /// Uses the vector index only when it is already loaded, and without counting as a read. This
    /// runs on every series page, every recommendation endpoint and the Home anime rail, so asking
    /// the index to load (or resetting its idle clock) kept 110 MB resident for as long as the reader
    /// had a never-show list. Otherwise the candidates' own genres and tags are read from the dump.
    /// </para>
    /// </summary>
    public async Task<Func<long, bool>?> PredicateAsync(IEnumerable<long> candidates, CancellationToken ct = default)
    {
        if (await TermsAsync(ct) is not { Count: > 0 } terms)
        {
            return null;
        }

        if (vectorIndex.TryGetLoaded() is { } index)
        {
            var plan = index.Plan(new RecommendationFilters(Hidden: terms));
            return id => index.TryGetRow(id, out var row) && !index.Matches(row, plan);
        }

        var hidden = await store.GetHiddenIdsAsync(candidates.ToList(), terms, ct);
        return hidden.Contains;
    }

    /// <inheritdoc cref="PredicateAsync(IEnumerable{long}, CancellationToken)"/>
    public Task<Func<long, bool>?> PredicateAsync(
        IEnumerable<MangaBakaRecommendation> candidates, CancellationToken ct = default) =>
        PredicateAsync(Ids(candidates), ct);

    private static IEnumerable<long> Ids(IEnumerable<MangaBakaRecommendation> items)
    {
        foreach (var item in items)
        {
            if (long.TryParse(item.ProviderId, out var id))
            {
                yield return id;
            }
        }
    }

    /// <summary>Drops hidden items from a list of catalogue picks.</summary>
    public static IReadOnlyList<MangaBakaRecommendation> Without(
        IReadOnlyList<MangaBakaRecommendation> items, Func<long, bool>? hidden) =>
        hidden is null
            ? items
            : items.Where(x => !long.TryParse(x.ProviderId, out var id) || !hidden(id)).ToList();
}
