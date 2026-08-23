namespace Maki.Metadata.Catalogue;

/// <summary>
/// Decides whether a plain free-text query is naming a person, without any <c>author:</c> prefix.
///
/// <para>
/// A static rather than a method on the searcher, because this is the part of the credit feature
/// most likely to go wrong and the only part that can be tested without loading an embedding model.
/// </para>
///
/// <para>
/// Note the asymmetry with an explicit prefix, which is deliberate and worth keeping: a typed
/// <c>author:</c> is a <em>filter</em>, because the user said what they meant, while a bare name is
/// only a <em>channel</em>, because "monster naoki urasawa" wants the title as much as the author.
/// </para>
/// </summary>
public static class CreditChannel
{
    /// <summary>
    /// The creator this query is probably naming, or null.
    /// </summary>
    /// <param name="maxWorks">
    /// Reject anyone credited on more works than this. It is the whole specificity guard: publishers
    /// hold five figures of credits (Kodansha 13,334, Shueisha 12,216) where the most prolific
    /// individual creators hold tens (Junji Itou 83, Naoki Urasawa 42), so one threshold separates
    /// "somebody made this" from "somebody printed a tenth of the catalogue".
    /// </param>
    public static CreditMatch? Select(
        IReadOnlyList<string> tokens, CreditIndex index, int maxWorks, int minRunChars, int minRunTokens)
    {
        if (index.IsEmpty || tokens.Count == 0)
        {
            return null;
        }

        if (!index.TryMatchLongestRun(tokens, CreditRole.Creator, minRunChars, minRunTokens, out var match, out _, out _))
        {
            return null;
        }

        return match.WorkCount > 0 && match.WorkCount <= maxWorks ? match : null;
    }
}
