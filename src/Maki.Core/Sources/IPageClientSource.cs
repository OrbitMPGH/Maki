namespace Maki.Core.Sources;

/// <summary>
/// Optional on an <see cref="ISource"/> whose page images live at an origin the admin configured
/// (a self-hosted server, usually on the LAN) rather than on a scraped site.
/// <c>PageDownloader</c> then fetches its pages with <see cref="PageHttpClientName"/> instead of
/// the <c>pages</c> client, and asks <see cref="IsPageUrlAllowed"/> instead of
/// <c>PublicAddressGuard</c>.
/// <para>
/// That check is the only thing standing in for the SSRF guard, so it must refuse every URL outside
/// the one configured origin. It is safe only because that origin was typed by an admin, the same
/// as the Kavita and FlareSolverr URLs; never implement this for a source whose page URLs come
/// from the site itself.
/// </para>
/// </summary>
public interface IPageClientSource
{
    string PageHttpClientName { get; }

    bool IsPageUrlAllowed(Uri url);
}
