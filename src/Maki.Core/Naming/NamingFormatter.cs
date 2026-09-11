using System.Text.RegularExpressions;

namespace Maki.Core.Naming;

/// <summary>
/// Renders a user-configured naming format. Pure: everything it needs arrives in the
/// <see cref="NamingContext"/>, so the same code runs for the settings preview and for the real
/// file on disk.
///
/// <para>
/// A token is <c>{Series Title}</c>. Lookup ignores the separators between the words and the
/// casing, so <c>{Series.Title}</c>, <c>{series_title}</c> and <c>{SERIESTITLE}</c> all resolve —
/// but the spelling is not thrown away: the separator used inside the token replaces the spaces in
/// the rendered value, and an all-lower or all-upper token lowercases or uppercases it. That is
/// what the Separator and Case pickers in the token modal produce; the formatter needs no state
/// of its own for them.
/// </para>
///
/// <para>
/// There is no syntax for "drop this bit when the value is missing". Absent values render empty
/// and the cleanup pass below (collapse whitespace, trim stray joining punctuation) is what keeps
/// the result tidy — which is why combo tokens like <c>{Series TitleYear}</c> and
/// <c>{Chapter VolChap}</c> exist: they carry their own punctuation and vanish whole.
/// </para>
/// </summary>
public static class NamingFormatter
{
    private static readonly Regex TokenPattern = new(@"\{([^{}]*)\}", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex EmptyBrackets = new(@"\(\s*\)|\[\s*\]", RegexOptions.Compiled);

    /// <summary>Characters that only ever join two parts together, so they're meaningless at an edge.</summary>
    private static readonly char[] JoiningChars = [' ', '-', '_', ','];

    public static string Format(string template, NamingContext context)
    {
        var rendered = TokenPattern.Replace(template, match =>
        {
            var (name, padding) = SplitPadding(match.Groups[1].Value);
            var token = NamingTokens.Find(name);
            if (token is null)
            {
                // Validation refuses unknown tokens at save time, so reaching here means a format
                // that predates a token being removed. Emitting the raw token text into a filename
                // would be worse than dropping it.
                return string.Empty;
            }

            var value = token.Resolve(context, token.SupportsPadding ? padding : null);
            return string.IsNullOrEmpty(value) ? string.Empty : ApplySpelling(value, name);
        });

        return FileNameSanitizer.Sanitize(Cleanup(rendered));
    }

    /// <summary>
    /// Every reason this format can't be saved, in the order they were found. Empty means good.
    /// </summary>
    public static IReadOnlyList<string> Validate(string? template)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(template))
        {
            errors.Add("Format cannot be empty");
            return errors;
        }

        if (template.Contains('/') || template.Contains('\\'))
        {
            errors.Add("Format cannot contain path separators — names are a single folder or file deep");
        }

        if (template.Contains(".."))
        {
            errors.Add("Format cannot contain \"..\"");
        }

        errors.AddRange(BraceErrors(template));

        var matches = TokenPattern.Matches(template);
        if (matches.Count == 0)
        {
            errors.Add("Format must contain at least one token, or every series would get the same name");
        }

        foreach (Match match in matches)
        {
            var (name, padding) = SplitPadding(match.Groups[1].Value);
            var token = NamingTokens.Find(name);
            if (token is null)
            {
                errors.Add($"Unknown token: {{{match.Groups[1].Value}}}");
                continue;
            }

            if (padding is null)
            {
                continue;
            }

            if (!token.SupportsPadding)
            {
                errors.Add($"{token.Display} does not take a padding pattern");
            }
            else if (padding.Length == 0 || padding.Any(c => c != '0'))
            {
                errors.Add($"Padding for {token.Display} must be zeroes, e.g. {{{name}:000}}");
            }
        }

        return errors;
    }

    /// <summary>Splits <c>Chapter Number:000</c> into its name and its padding pattern.</summary>
    private static (string Name, string? Padding) SplitPadding(string body)
    {
        var colon = body.IndexOf(':');
        return colon < 0
            ? (body.Trim(), null)
            : (body[..colon].Trim(), body[(colon + 1)..].Trim());
    }

    /// <summary>Applies the separator and casing the token was spelled with to its value.</summary>
    private static string ApplySpelling(string value, string tokenName)
    {
        var separator = tokenName.FirstOrDefault(c => NamingTokens.Separators.Contains(c) && c != ' ');
        if (separator != default)
        {
            value = value.Replace(' ', separator);
        }

        var letters = tokenName.Where(char.IsLetter).ToArray();
        if (letters.Length == 0)
        {
            return value;
        }

        if (letters.All(char.IsLower))
        {
            return value.ToLowerInvariant();
        }

        return letters.All(char.IsUpper) ? value.ToUpperInvariant() : value;
    }

    private static IEnumerable<string> BraceErrors(string template)
    {
        var open = false;
        foreach (var c in template)
        {
            switch (c)
            {
                case '{' when open:
                    yield return "Format has a \"{\" inside a token";
                    yield break;
                case '{':
                    open = true;
                    break;
                case '}' when !open:
                    yield return "Format has a \"}\" with no matching \"{\"";
                    yield break;
                case '}':
                    open = false;
                    break;
            }
        }

        if (open)
        {
            yield return "Format has a \"{\" with no matching \"}\"";
        }
    }

    /// <summary>
    /// Renders a single token on its own, for the token picker's example column. Blank stays blank
    /// here rather than becoming Sanitize's "_" placeholder: a token with no value for the sample
    /// series is exactly what the column needs to show.
    /// </summary>
    public static string ExampleFor(NamingToken token, NamingContext context)
    {
        var value = token.Resolve(context, null);
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : FileNameSanitizer.Sanitize(Cleanup(value));
    }

    /// <summary>
    /// Tidies up after the values that came out empty. Brackets are handled here because they're
    /// the one bit of punctuation that can't be trimmed off an edge, and wrapping an id or a year
    /// in them is the obvious thing to write — so an empty pair goes rather than leaving
    /// "Berserk []".
    /// </summary>
    private static string Cleanup(string rendered)
    {
        rendered = EmptyBrackets.Replace(rendered, string.Empty);
        return Whitespace.Replace(rendered, " ").Trim().Trim(JoiningChars);
    }
}
