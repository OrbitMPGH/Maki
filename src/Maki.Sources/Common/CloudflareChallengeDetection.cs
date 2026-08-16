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

            if (title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
                || content.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase)
                || content.Contains("cf-challenge", StringComparison.OrdinalIgnoreCase)
                || content.Contains("__cf_chl", StringComparison.OrdinalIgnoreCase))
            {
                return PageVerdict.Challenge;
            }

            return PageVerdict.Unknown;
        }
        catch (PlaywrightException)
        {
            return PageVerdict.Unknown;
        }
    }
}
