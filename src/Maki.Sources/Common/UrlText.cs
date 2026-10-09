using System.Text;

namespace Maki.Sources.Common;

internal static class UrlText
{
    /// <summary>Title lowercased with every run of non-[a-z0-9] collapsed to a single <paramref name="separator"/>.</summary>
    public static string Slugify(string title, char separator)
    {
        var slug = new StringBuilder(title.Length);
        foreach (var c in title.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != separator)
            {
                slug.Append(separator);
            }
        }

        return slug.ToString().Trim(separator);
    }

    /// <summary>The unescaped value of query parameter <paramref name="key"/> (case-insensitive), or null.</summary>
    public static string? QueryValue(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=');
            if (split > 0 && pair[..split].Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[(split + 1)..]);
            }
        }

        return null;
    }
}
