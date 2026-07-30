namespace Maki.Core.Sources;

/// <summary>
/// Decides whether a caller-supplied cover URL may be fetched on the server's behalf.
/// <para>
/// The cover proxy exists because several image CDNs hotlink-block any Referer but the site's own, so
/// the server has to make the request. That makes it a server-side fetch of a URL the client chose —
/// an SSRF primitive — and the only thing standing between it and the host's private network is this
/// check. Fail closed: an unrecognised host is refused, not fetched and then judged.
/// </para>
/// </summary>
public static class CoverHostPolicy
{
    /// <summary>
    /// Whether <paramref name="target"/>'s host belongs to <paramref name="source"/>: its own domain
    /// (subdomains included, so <c>uploads.mangadex.org</c> passes for <c>mangadex.org</c>) or one of
    /// the extra CDN domains the source declares in <see cref="ISource.CoverHosts"/>.
    /// </summary>
    public static bool Allows(ISource source, Uri target)
    {
        if (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        // "BaseUrl host minus www" rather than a public-suffix-list registrable domain. It is
        // deliberately the tighter of the two: for mangaplus.shueisha.co.jp this permits only that
        // subtree, where a registrable-domain rule would open all of shueisha.co.jp. It never opens
        // more than the source's own name.
        if (!Uri.TryCreate(source.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            return false;
        }

        var ownDomain = StripWww(baseUri.Host);
        if (IsWithin(target.Host, ownDomain))
        {
            return true;
        }

        foreach (var extra in source.CoverHosts)
        {
            if (IsWithin(target.Host, StripWww(extra)))
            {
                return true;
            }
        }

        return false;
    }

    private static string StripWww(string host) =>
        host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

    /// <summary>
    /// Exact match or a subdomain. The leading dot matters: without it, "evil-mangadex.org" would
    /// satisfy a suffix test for "mangadex.org".
    /// </summary>
    private static bool IsWithin(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith('.' + domain, StringComparison.OrdinalIgnoreCase);
}
