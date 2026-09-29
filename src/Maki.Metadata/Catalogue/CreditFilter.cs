using Maki.Metadata.MangaBaka;

namespace Maki.Metadata.Catalogue;

/// <summary>
/// Resolves <see cref="RecommendationFilters.Credits"/> into <see cref="RecommendationFilters.CreditIds"/>.
///
/// <para>
/// Every credit unions, whatever its role: a filter naming an author and a studio means work by
/// either of them. That is deliberately not the search box's <c>author:x studio:y</c>, which
/// intersects across roles, because a filter is a list someone builds up one name at a time and
/// the follow list is exactly that list. A name that resolves to nobody contributes nothing, and a
/// list where nobody resolves matches nothing rather than everything.
/// </para>
/// </summary>
public static class CreditFilter
{
    public static RecommendationFilters Resolve(RecommendationFilters filters, CreditIndex? index, int maxDistance)
    {
        if (filters.Credits is not { Count: > 0 } credits)
        {
            return filters.CreditIds is null ? filters : filters with { CreditIds = null };
        }

        return filters with { CreditIds = WorksOf(credits, index, maxDistance) };
    }

    /// <summary>Every work credited to any of <paramref name="credits"/>, most popular name first.</summary>
    private static long[] WorksOf(
        IReadOnlyList<Maki.Core.Recommendations.CatalogueCredit> credits, CreditIndex? index, int maxDistance)
    {
        if (index is null || index.IsEmpty)
        {
            return [];
        }

        var seen = new HashSet<long>();
        var works = new List<long>();
        foreach (var credit in credits)
        {
            var role = CreditIndex.ParseRole(credit.Role);
            if (!index.TryResolveFuzzy(credit.Name, role, maxDistance, out var nameId))
            {
                continue;
            }

            foreach (var id in index.WorksOf(nameId, role))
            {
                if (seen.Add(id))
                {
                    works.Add(id);
                }
            }
        }

        return [.. works];
    }
}
