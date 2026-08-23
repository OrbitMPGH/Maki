using System.Text;

namespace Maki.Metadata.Catalogue;

/// <summary>One <c>author:</c>-style term lifted out of a query.</summary>
public readonly record struct CreditTerm(CreditRole Roles, string Name);

/// <summary>
/// A search box split into the credits it names and the words left over.
///
/// <para>
/// The syntax is <c>author:</c>, <c>artist:</c>, <c>studio:</c> (with <c>publisher:</c> as an
/// alias) and <c>by:</c>, which covers writing and art together. A quoted value is exactly what is
/// between the quotes; an unquoted one runs to the next keyword or the end of the box, because
/// almost nobody types <c>author:"Junji Ito"</c> and <c>author:junji</c> plus a stray "ito" finds
/// the wrong person. <c>CreditResolver</c> hands back whatever of an unquoted value turned out not
/// to be part of a name, so <c>author:junji ito uzumaki</c> still searches for Uzumaki.
/// </para>
///
/// <para>
/// A <c>word:</c> sequence is only read as a keyword when <c>word</c> is one of those five and it
/// starts a token. That restriction is the whole reason this is a hand-rolled scan rather than a
/// split on colons: "Kaguya-sama: Love is War" is a title, not a field, and so is every other
/// subtitled series in the catalogue.
/// </para>
/// </summary>
public sealed record CatalogueQuery(string FreeText, IReadOnlyList<CreditTerm> Credits)
{
    public static readonly CatalogueQuery Empty = new(string.Empty, []);

    private static readonly (string Keyword, CreditRole Roles)[] Keywords =
    [
        ("author", CreditRole.Author),
        ("artist", CreditRole.Artist),
        ("studio", CreditRole.Publisher),
        ("publisher", CreditRole.Publisher),
        ("by", CreditRole.Creator),
    ];

    public bool HasCredits => Credits.Count > 0;

    public bool HasFreeText => FreeText.Length > 0;

    public static CatalogueQuery Parse(string? query)
    {
        var text = query?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return Empty;
        }

        // Nothing to scan for in the common case, and this keeps an ordinary title query off the
        // character-by-character path entirely.
        if (!text.Contains(':', StringComparison.Ordinal))
        {
            return new CatalogueQuery(text, []);
        }

        var free = new StringBuilder(text.Length);
        var credits = new List<CreditTerm>();

        var i = 0;
        while (i < text.Length)
        {
            if ((i == 0 || char.IsWhiteSpace(text[i - 1])) && TryReadKeyword(text, i, out var roles, out var valueAt))
            {
                var end = ReadValue(text, valueAt, out var value);
                if (value.Length > 0)
                {
                    credits.Add(new CreditTerm(roles, value));
                }

                i = end;
                continue;
            }

            free.Append(text[i]);
            i++;
        }

        return new CatalogueQuery(Collapse(free), credits);
    }

    private static bool TryReadKeyword(string text, int at, out CreditRole roles, out int valueAt)
    {
        foreach (var (keyword, keywordRoles) in Keywords)
        {
            var span = text.AsSpan(at);
            if (span.Length > keyword.Length &&
                span[..keyword.Length].Equals(keyword, StringComparison.OrdinalIgnoreCase) &&
                span[keyword.Length] == ':')
            {
                roles = keywordRoles;
                valueAt = at + keyword.Length + 1;
                return true;
            }
        }

        roles = CreditRole.None;
        valueAt = at;
        return false;
    }

    /// <summary>Reads the keyword's value and returns the index just past it.</summary>
    private static int ReadValue(string text, int at, out string value)
    {
        if (at < text.Length && text[at] == '"')
        {
            var close = text.IndexOf('"', at + 1);
            if (close < 0)
            {
                // An unbalanced quote takes the rest of the box, which is what someone still typing
                // the closing quote means.
                value = text[(at + 1)..].Trim();
                return text.Length;
            }

            value = text[(at + 1)..close].Trim();
            return close + 1;
        }

        // Unquoted: everything up to the next keyword. One token would split "author:junji ito"
        // into a search for somebody called Junji, which the dump does have, with one work to his
        // name. Resolution gives back any trailing words that were not part of a name.
        var end = at;
        var i = at;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i >= text.Length || TryReadKeyword(text, i, out _, out _))
            {
                break;
            }

            while (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            end = i;
        }

        value = text[at..end].Trim();
        return end;
    }

    /// <summary>Squeezes the holes a lifted keyword leaves behind out of the remaining text.</summary>
    private static string Collapse(StringBuilder builder)
    {
        var result = new StringBuilder(builder.Length);
        var pendingSpace = false;
        for (var i = 0; i < builder.Length; i++)
        {
            if (char.IsWhiteSpace(builder[i]))
            {
                pendingSpace = result.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }

            result.Append(builder[i]);
        }

        return result.ToString();
    }
}
