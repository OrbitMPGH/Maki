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

    /// <summary>
    /// A scraped href resolved against <paramref name="baseUrl"/>, or null unless it is http(s) and, with
    /// <paramref name="requireSiteHost"/>, on the site's own host. <c>Uri.TryCreate(href, UriKind.Absolute)</c>
    /// reads "/path" as a file:/// URI on Linux, so it cannot be used to tell a relative href from an absolute one.
    /// </summary>
    public static Uri? ResolveHref(string baseUrl, string? href, bool requireSiteHost = true)
    {
        var baseUri = new Uri(baseUrl);
        if (string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(baseUri, href.Trim(), out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        if (!requireSiteHost)
        {
            return uri;
        }

        var baseHost = baseUri.Host;
        return uri.Host.Equals(baseHost, StringComparison.OrdinalIgnoreCase) ||
               uri.Host.Equals($"www.{baseHost}", StringComparison.OrdinalIgnoreCase) ||
               baseHost.Equals($"www.{uri.Host}", StringComparison.OrdinalIgnoreCase)
            ? uri
            : null;
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
