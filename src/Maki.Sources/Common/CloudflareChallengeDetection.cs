using Microsoft.Playwright;

namespace Maki.Sources.Common;

/// <summary>Raised when a page comes back stuck on a Cloudflare challenge or block, to trigger a re-solve and retry.</summary>
public sealed class ChallengeException(string? message = null) : Exception(message);

public enum PageVerdict
{
    /// <summary>Not obviously Cloudflare's doing.</summary>
    Unknown,

    /// <summary>The solvable JS interstitial ("Just a moment") — stale clearance, worth re-solving.</summary>
    Challenge,

    /// <summary>A firewall/bot-score block — re-solving alone won't clear it.</summary>
    Blocked,
}

/// <summary>
/// Cloudflare challenge/block-page detection shared by every source that drives its own Playwright
/// browser through a FlareSolverr-solved session (<c>MangaFireBrowser</c>, <c>TopManhuaImageBrowser</c>).
/// The JS challenge interstitial's markers are the same everywhere; the firewall block page's wording
/// varies per site, so callers supply their own.
/// </summary>
public static class CloudflareChallengeDetection
{
    public static async Task<PageVerdict> ClassifyAsync(
        IPage page, IReadOnlyList<string> blockedTitleContains, IReadOnlyList<string> blockedContentContains)
    {
        try
        {
            var title = await page.TitleAsync();
            var content = await page.ContentAsync();

            if (blockedTitleContains.Any(s => title.Contains(s, StringComparison.OrdinalIgnoreCase))
                || blockedContentContains.Any(s => content.Contains(s, StringComparison.OrdinalIgnoreCase)))
            {
                return PageVerdict.Blocked;
            }

            return IsChallenge(title, content) ? PageVerdict.Challenge : PageVerdict.Unknown;
        }
        catch (PlaywrightException)
        {
            return PageVerdict.Unknown;
        }
    }

    // Not "challenge-platform": Cloudflare's JS detections inject that path into healthy pages.
    internal static bool IsChallenge(string title, string content) =>
        title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
        || content.Contains("cf-challenge", StringComparison.OrdinalIgnoreCase)
        || content.Contains("cf-chl", StringComparison.OrdinalIgnoreCase)
        || content.Contains("__cf_chl", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Throws when a raw body fetched through <c>IHtmlFetcher</c> is the challenge interstitial
    /// rather than the page. The fetcher hands a recognised challenge to FlareSolverr itself; this
    /// is the backstop for a shell that reaches the parser anyway, so it fails loudly instead of
    /// reading as an empty search or an empty chapter list. Real pages are far bigger than the
    /// shell, so a large body is never mistaken for one.
    /// </summary>
    public static void ThrowIfChallenge(string html, string url)
    {
        if (html.Length <= 20_000 && IsChallenge(html, html))
        {
            throw new InvalidOperationException(
                $"{url} answered with a Cloudflare challenge page instead of content");
        }
    }
}
