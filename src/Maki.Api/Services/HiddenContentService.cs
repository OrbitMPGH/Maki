using Maki.Core.Configuration;
using Maki.Core.Recommendations;
using Maki.Metadata.Embedding;
using Maki.Metadata.MangaBaka;

namespace Maki.Api.Services;

/// <summary>
/// The caller's never-show list (<see cref="HiddenContentSpec"/>), applied two ways: folded into a
/// request's filters so it narrows before a page is cut, and as a predicate over the shared rails,
/// which are cached instance-wide and so cannot carry one reader's list into their build.
/// </summary>
public class HiddenContentService(IUserSettings userSettings, VectorIndexCache vectorIndex)
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
    /// The filters with the caller's list in <see cref="RecommendationFilters.Hidden"/>. Always
    /// overwrites: the list is a setting, and a request cannot choose to see past it.
    /// </summary>
    public async Task<RecommendationFilters> ApplyAsync(RecommendationFilters filters, CancellationToken ct = default) =>
        filters with { Hidden = await TermsAsync(ct) };

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
        };

    /// <summary>
    /// True for a MangaBaka id the caller has hidden. Null when nothing is hidden, or when the index
    /// is not built: tags cannot be tested without it, and a row missing from it has nothing to
    /// test, so both read as visible.
    /// </summary>
    public async Task<Func<long, bool>?> PredicateAsync(CancellationToken ct = default)
    {
        if (await TermsAsync(ct) is not { Count: > 0 } terms || await vectorIndex.GetAsync(ct) is not { } index)
        {
            return null;
        }

        var plan = index.Plan(new RecommendationFilters(Hidden: terms));
        return id => index.TryGetRow(id, out var row) && !index.Matches(row, plan);
    }

    /// <summary>Drops hidden items from a list of catalogue picks.</summary>
    public static IReadOnlyList<MangaBakaRecommendation> Without(
        IReadOnlyList<MangaBakaRecommendation> items, Func<long, bool>? hidden) =>
        hidden is null
            ? items
            : items.Where(x => !long.TryParse(x.ProviderId, out var id) || !hidden(id)).ToList();
}
