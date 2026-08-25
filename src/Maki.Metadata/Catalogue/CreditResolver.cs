namespace Maki.Metadata.Catalogue;

/// <summary>A credit a query named, resolved to a real entry, for display as a chip.</summary>
public sealed record ResolvedCredit(string Name, IReadOnlyList<string> Roles, int WorkCount);

/// <summary>
/// What the <c>author:</c>-style terms in a query narrow the catalogue to.
/// </summary>
/// <param name="SeriesIds">
/// Null means the query named nobody, so nothing is narrowed. An empty array means it named
/// somebody who does not exist, or a combination nobody satisfies, which is a real answer of
/// "nothing" rather than a reason to fall back to searching everything.
/// </param>
/// <param name="ExtraFreeText">
/// Words from an unquoted credit value that turned out not to be part of the name, handed back so
/// the caller can search for them. <c>author:junji ito uzumaki</c> resolves Junji Itou and returns
/// "uzumaki" here.
/// </param>
public sealed record CreditResolution(
    long[]? SeriesIds, IReadOnlyList<ResolvedCredit> Credits, string ExtraFreeText = "")
{
    public static readonly CreditResolution None = new(null, []);

    public bool Restricts => SeriesIds is not null;

    public bool Impossible => SeriesIds is { Length: 0 };
}

/// <summary>
/// Turns the credit terms of a <see cref="CatalogueQuery"/> into the set of series they allow.
///
/// <para>
/// Terms naming the same role union, and different roles intersect: <c>author:x author:y</c> is
/// "either of them", while <c>author:x studio:y</c> is "his work, published by them". That is how
/// the two read in English, and it is the only combination that lets someone widen and narrow with
/// the same syntax.
/// </para>
///
/// <para>
/// A term that resolves to nobody makes the whole query impossible rather than being dropped.
/// Silently ignoring a misspelled name would answer a search for one author's work with the entire
/// catalogue, which reads as the filter having been ignored, because it was.
/// </para>
/// </summary>
public static class CreditResolver
{
    public static CreditResolution Resolve(CatalogueQuery query, CreditIndex index, CatalogueOptions options)
    {
        if (!query.HasCredits || index.IsEmpty)
        {
            return CreditResolution.None;
        }

        var credits = new List<ResolvedCredit>(query.Credits.Count);
        var leftover = new List<string>();
        List<long>? intersection = null;

        foreach (var group in query.Credits.GroupBy(c => c.Roles))
        {
            var union = new HashSet<long>();
            var resolvedAny = false;

            foreach (var term in group)
            {
                if (!TryResolveTerm(index, term, group.Key, options, out var nameId, leftover))
                {
                    continue;
                }

                resolvedAny = true;
                credits.Add(new ResolvedCredit(
                    index.NameAt(nameId), index.RoleLabelsAt(nameId), index.WorkCountOf(nameId, group.Key)));
                union.UnionWith(index.WorksOf(nameId, group.Key));
            }

            if (!resolvedAny)
            {
                return new CreditResolution([], credits, string.Join(' ', leftover));
            }

            if (intersection is null)
            {
                // Keep the first group's order, which is popularity, so a truncated set keeps the
                // works anyone has heard of.
                intersection = [.. Order(index, query.Credits[0], union)];
            }
            else
            {
                intersection.RemoveAll(id => !union.Contains(id));
            }

            if (intersection.Count == 0)
            {
                return new CreditResolution([], credits, string.Join(' ', leftover));
            }
        }

        if (intersection is null)
        {
            return CreditResolution.None;
        }

        var capped = intersection.Count > options.CreditSqlIdCap
            ? intersection.Take(options.CreditSqlIdCap)
            : intersection;

        return new CreditResolution([.. capped], credits, string.Join(' ', leftover));
    }

    /// <summary>
    /// Resolves one term, first as written and then, if that fails, as the longest run of its words
    /// that does name somebody. The words left over go to <paramref name="leftover"/>, which is what
    /// makes an unquoted <c>author:junji ito uzumaki</c> behave the way it reads.
    ///
    /// <para>
    /// A single-word run is allowed here, unlike in <see cref="CreditChannel"/>: the user typed
    /// <c>author:</c>, so they have already said the word is a name.
    /// </para>
    /// </summary>
    private static bool TryResolveTerm(
        CreditIndex index, CreditTerm term, CreditRole roles, CatalogueOptions options,
        out int nameId, List<string> leftover)
    {
        if (index.TryResolveFuzzy(term.Name, roles, options.CreditResolveMaxDistance, out nameId))
        {
            return true;
        }

        var tokens = CatalogueText.Tokenize(term.Name);
        if (tokens.Length > 1 &&
            index.TryMatchLongestRun(tokens, roles, minRunChars: 2, minRunTokens: 1, out var match, out var start, out var length))
        {
            nameId = match.NameId;
            for (var i = 0; i < tokens.Length; i++)
            {
                if (i < start || i >= start + length)
                {
                    leftover.Add(tokens[i]);
                }
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Puts a unioned set back into popularity order by walking the first term's own work list,
    /// which <see cref="CreditIndex"/> already stores that way, then appending anything the other
    /// terms contributed.
    /// </summary>
    private static IEnumerable<long> Order(CreditIndex index, CreditTerm first, HashSet<long> union)
    {
        if (!index.TryResolve(first.Name, first.Roles, out var nameId))
        {
            return union;
        }

        var ordered = new List<long>(union.Count);
        var seen = new HashSet<long>(union.Count);
        foreach (var id in index.WorksOf(nameId, first.Roles))
        {
            if (union.Contains(id) && seen.Add(id))
            {
                ordered.Add(id);
            }
        }

        foreach (var id in union)
        {
            if (seen.Add(id))
            {
                ordered.Add(id);
            }
        }

        return ordered;
    }
}
