namespace Maki.Core.Recommendations;

/// <summary>
/// One genre or tag inside a <see cref="CatalogueRule"/>. <paramref name="Kind"/> is
/// <see cref="CatalogueRules.Genre"/> or <see cref="CatalogueRules.Tag"/>; the two options only
/// mean something for tags and are ignored on a genre.
/// </summary>
/// <param name="Subtags">Also matches every tag below this one in MangaBaka's tag tree, so
/// "School" covers "College" and "Magic School".</param>
/// <param name="Central">Only counts where the tag is core or defining for the series, not a
/// recurrent or passing element.</param>
public record CatalogueTerm(string Kind, string Name, bool Subtags = false, bool Central = false);

/// <summary>
/// A group of terms with one of three modes: the series carries every term, at least one, or none.
/// A filter's rules are ANDed together, which covers "Romance, and School or College, but no adult
/// cast" without a nested expression tree.
/// </summary>
public record CatalogueRule(string Mode, IReadOnlyList<CatalogueTerm> Terms);

public static class CatalogueRules
{
    public const string Genre = "genre";
    public const string Tag = "tag";

    public const string All = "all";
    public const string Any = "any";
    public const string None = "none";

    private const int MaxRules = 20;
    private const int MaxTerms = 60;

    /// <summary>
    /// Drops what cannot mean anything (blank names, unknown kinds or modes, empty rules) and caps
    /// the sizes, so a hand-rolled request cannot park an unbounded blob in a saved spec or make
    /// every row test walk a thousand terms. Null when nothing survives.
    /// </summary>
    public static IReadOnlyList<CatalogueRule>? Normalize(IReadOnlyList<CatalogueRule>? rules)
    {
        if (rules is null)
        {
            return null;
        }

        var kept = new List<CatalogueRule>();
        foreach (var rule in rules)
        {
            var mode = rule?.Mode?.ToLowerInvariant();
            if (mode is not (All or Any or None))
            {
                continue;
            }

            var terms = NormalizeTerms(rule!.Terms);
            if (terms is not null)
            {
                kept.Add(new CatalogueRule(mode, terms));
            }

            if (kept.Count == MaxRules)
            {
                break;
            }
        }

        return kept.Count == 0 ? null : kept;
    }

    public static IReadOnlyList<CatalogueTerm>? NormalizeTerms(IReadOnlyList<CatalogueTerm>? terms)
    {
        if (terms is null)
        {
            return null;
        }

        var kept = new List<CatalogueTerm>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in terms)
        {
            var kind = term?.Kind?.ToLowerInvariant();
            if (kind is not (Genre or Tag) || string.IsNullOrWhiteSpace(term!.Name))
            {
                continue;
            }

            var name = term.Name.Trim();
            if (!seen.Add($"{kind}:{name}"))
            {
                continue;
            }

            kept.Add(kind == Genre
                ? new CatalogueTerm(kind, name)
                : new CatalogueTerm(kind, name, term.Subtags, term.Central));
            if (kept.Count == MaxTerms)
            {
                break;
            }
        }

        return kept.Count == 0 ? null : kept;
    }

    /// <summary>A stable text form, for cache keys.</summary>
    public static string Key(IReadOnlyList<CatalogueRule>? rules) =>
        rules is null
            ? string.Empty
            : string.Join('|', rules.Select(r => $"{r.Mode}:{TermsKey(r.Terms)}"));

    public static string TermsKey(IReadOnlyList<CatalogueTerm>? terms) =>
        terms is null
            ? string.Empty
            : string.Join(',', terms.Select(t => $"{t.Kind}/{t.Name}/{(t.Subtags ? 1 : 0)}{(t.Central ? 1 : 0)}"));
}
