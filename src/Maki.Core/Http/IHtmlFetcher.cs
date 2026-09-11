namespace Maki.Core.Http;

/// <summary>
/// The one thing a scraper needs from <see cref="ChallengeAwareFetcher"/>: fetch a URL's body,
/// solving an anti-bot challenge first if the site puts one in the way.
/// <para>
/// Sources behind Cloudflare take this rather than the concrete fetcher so their parsers can be
/// tested against recorded fixtures — <see cref="ChallengeAwareFetcher"/> pulls in FlareSolverr and
/// app settings, neither of which a parser test has any use for.
/// </para>
/// </summary>
public interface IHtmlFetcher
{
    Task<string> GetHtmlAsync(string url, CancellationToken ct = default);
}
