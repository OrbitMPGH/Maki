namespace Maki.Core.Http;

/// <summary>
/// The one thing a scraper needs from <see cref="ChallengeAwareFetcher"/>: fetch a URL's body,
/// solving an anti-bot challenge first if the site puts one in the way.
/// <para>
/// Sources behind Cloudflare take this rather than the concrete fetcher so their parsers can be
/// tested against recorded fixtures: <see cref="ChallengeAwareFetcher"/> pulls in FlareSolverr and
/// app settings, neither of which a parser test has any use for.
/// </para>
/// </summary>
public interface IHtmlFetcher
{
    Task<string> GetHtmlAsync(string url, CancellationToken ct = default);

    /// <summary>
    /// Like <see cref="GetHtmlAsync"/> but with extra cookies and an optional form body. Both travel
    /// on the direct request and into FlareSolverr's browser, so a site whose search is a POST or
    /// whose catalogue hides behind an opt-in cookie (Toonily's <c>toonily-mature=1</c>) answers the
    /// same way through either path.
    /// </summary>
    Task<string> FetchAsync(HtmlFetchRequest request, CancellationToken ct = default);
}

/// <summary>
/// One fetch through <see cref="IHtmlFetcher"/>. <see cref="FormBody"/>, when set, makes it a POST
/// with <c>application/x-www-form-urlencoded</c> content (the only body FlareSolverr accepts).
/// <see cref="Cookies"/> are sent in addition to any solved clearance cookies and win on a name clash.
/// </summary>
public record HtmlFetchRequest(
    string Url,
    IReadOnlyDictionary<string, string>? Cookies = null,
    string? FormBody = null);
