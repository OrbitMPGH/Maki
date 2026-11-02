using System.Text;
using System.Text.RegularExpressions;

namespace Maki.Sources.Custom;

/// <summary>
/// URL templates of a custom source: <c>{query}</c>, <c>{seriesId}</c> and <c>{chapterId}</c>,
/// relative to the base URL or absolute on its host. Ids are inserted as they are (they come out
/// of the site's own paths); the query is escaped.
/// </summary>
public static class CustomTemplate
{
    public const string Query = "{query}";
    public const string SeriesId = "{seriesId}";
    public const string ChapterId = "{chapterId}";

    private static readonly string[] Placeholders = [Query, SeriesId, ChapterId];

    public static Uri Expand(Uri baseUri, string template, string? seriesId = null, string? chapterId = null, string? query = null)
    {
        var text = template
            .Replace(SeriesId, seriesId ?? "", StringComparison.Ordinal)
            .Replace(ChapterId, chapterId ?? "", StringComparison.Ordinal)
            .Replace(Query, query is null ? "" : Uri.EscapeDataString(query), StringComparison.Ordinal);
        return new Uri(baseUri, text);
    }

    /// <summary>
    /// Reads <c>{seriesId}</c> back out of a URL the template made, or null when the URL is not one.
    /// The template's own host decides when it is absolute; otherwise the base URL's does.
    /// </summary>
    public static string? Match(Uri baseUri, string template, Uri url, string placeholder)
    {
        var templateUri = new Uri(baseUri, StripPlaceholders(template));
        if (!CustomHost.Same(templateUri.Host, url.Host))
        {
            return null;
        }

        var pattern = PathPattern(new Uri(baseUri, template.Replace("{", "%7B").Replace("}", "%7D")).AbsolutePath, placeholder);
        var match = Regex.Match(url.AbsolutePath, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        return match.Success && match.Groups["id"].Value is { Length: > 0 } id ? Uri.UnescapeDataString(id) : null;
    }

    public static bool Mentions(string template, string placeholder) =>
        template.Contains(placeholder, StringComparison.Ordinal);

    /// <summary>The path of a template with its placeholders taken out, for checking which host it names.</summary>
    private static string StripPlaceholders(string template)
    {
        var text = template;
        foreach (var p in Placeholders)
        {
            text = text.Replace(p, "x", StringComparison.Ordinal);
        }

        return text;
    }

    private static string PathPattern(string escapedPath, string placeholder)
    {
        var escapedPlaceholder = placeholder.Replace("{", "%7B").Replace("}", "%7D");
        var builder = new StringBuilder("^");
        var rest = escapedPath;
        var named = false;
        while (rest.Length > 0)
        {
            var next = NextPlaceholder(rest, out var length);
            if (next < 0)
            {
                builder.Append(Regex.Escape(rest));
                break;
            }

            builder.Append(Regex.Escape(rest[..next]));
            var found = rest.Substring(next, length);
            if (!named && found == escapedPlaceholder)
            {
                builder.Append("(?<id>[^?#]+?)");
                named = true;
            }
            else
            {
                builder.Append("[^/?#]+?");
            }

            rest = rest[(next + length)..];
        }

        // Trailing slash optional either way: sites link both forms.
        var pattern = builder.ToString();
        if (pattern.EndsWith('/'))
        {
            pattern = pattern[..^1];
        }

        return pattern + "/?$";
    }

    private static int NextPlaceholder(string text, out int length)
    {
        var best = -1;
        length = 0;
        foreach (var p in Placeholders)
        {
            var escaped = p.Replace("{", "%7B").Replace("}", "%7D");
            var at = text.IndexOf(escaped, StringComparison.Ordinal);
            if (at >= 0 && (best < 0 || at < best))
            {
                best = at;
                length = escaped.Length;
            }
        }

        return best;
    }
}

/// <summary>Host comparison that treats <c>www.example.com</c> and <c>example.com</c> as one site.</summary>
public static class CustomHost
{
    public static bool Same(string a, string b) =>
        string.Equals(Strip(a), Strip(b), StringComparison.OrdinalIgnoreCase);

    public static string Strip(string host) =>
        host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
}
