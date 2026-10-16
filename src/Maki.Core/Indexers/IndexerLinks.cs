namespace Maki.Core.Indexers;

public static class IndexerLinks
{
    private static readonly HashSet<string> KeyParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "apikey", "api_key", "jackett_apikey",
    };

    /// <summary>
    /// The link without its API key query parameters, otherwise byte for byte as given. A backend's
    /// proxy link carries the key, so a link that is shown to a user or kept as an identifier must
    /// pass through here first.
    /// </summary>
    public static string? WithoutApiKey(string? link)
    {
        if (link is null || link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return link;
        }

        var queryStart = link.IndexOf('?');
        if (queryStart < 0)
        {
            return link;
        }

        var fragmentStart = link.IndexOf('#', queryStart);
        var queryEnd = fragmentStart < 0 ? link.Length : fragmentStart;
        var parts = link[(queryStart + 1)..queryEnd].Split('&');
        var kept = parts.Where(p => !KeyParameters.Contains(p.Split('=', 2)[0])).ToArray();
        if (kept.Length == parts.Length)
        {
            return link;
        }

        var query = kept.Length == 0 ? string.Empty : "?" + string.Join('&', kept);
        return link[..queryStart] + query + link[queryEnd..];
    }
}
