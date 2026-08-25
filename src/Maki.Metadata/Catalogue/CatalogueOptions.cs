namespace Maki.Metadata.Catalogue;

/// <summary>
/// Everything the catalogue indexes need to be told, in one record so the store takes a single
/// optional dependency instead of one per feature. <c>SearchTuning</c> holds an instance of this and
/// <c>distribution/eval-search.cs</c> sweeps through it with dotted keys.
/// </summary>
public sealed record CatalogueOptions
{
    public static readonly CatalogueOptions Default = new();

    /// <summary>Typo tolerance for the title index.</summary>
    public FuzzyOptions Fuzzy { get; init; } = FuzzyOptions.Default;

    /// <summary>
    /// Edits allowed when resolving an explicit <c>author:</c> value against the name list, so
    /// <c>author:junji itoo</c> still lands. Kept low on purpose: a stated name is not a guess, and
    /// a generous budget here turns it into one.
    /// </summary>
    public int CreditResolveMaxDistance { get; init; } = 1;

    /// <summary>
    /// Most series ids a credit filter will inline into a SQL <c>IN (…)</c>. <see cref="CreditIndex"/>
    /// stores each name's works in popularity order, so a truncated <c>studio:kodansha</c> keeps the
    /// 5,000 works anyone has heard of rather than the 5,000 with the lowest ids. The in-memory
    /// search path has no such limit; this only bounds the statement the dump has to parse.
    /// </summary>
    public int CreditSqlIdCap { get; init; } = 5000;
}
