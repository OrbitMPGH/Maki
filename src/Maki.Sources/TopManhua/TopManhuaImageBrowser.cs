using System.Text.RegularExpressions;
using Maki.Core.Http;
using Maki.Sources.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Maki.Sources.TopManhua;

/// <summary>
/// Fetches a chapter's page images through a real browser instead of a plain HTTP request. The
/// image CDN (img-r2.2xstorage.com) sits behind Cloudflare bot management and blocks a bare
/// re-request for an image URL with a "Sorry, you have been blocked" page — even from a real
/// browser typed into the address bar — while the exact same image loads fine as an embedded
/// &lt;img&gt; on the chapter page. Headers alone don't close that gap, and neither does a bare
/// Playwright Chromium on its own: headless automation carries its own tells (WebGL renderer,
/// CDP artifacts, missing plugins) that Cloudflare's client-side bot-detection script picks up
/// regardless of what headers are sent. So this reuses <see cref="ChallengeAwareFetcher"/>'s
/// FlareSolverr-solved session the same way <c>MangaFireBrowser</c> does — FlareSolverr's browser
/// is specifically hardened against headless detection — then drives our own Chromium with that
/// session's cookies/UA to load the chapter page and let its native image pipeline fetch pages,
/// capturing the bytes off the network as they come in.
/// </summary>
public sealed class TopManhuaImageBrowser(
    ChallengeAwareFetcher fetcher,
    ILogger<TopManhuaImageBrowser> logger) : IAsyncDisposable
{
    private const string BaseUrl = "https://www.topmanhua.fan";
    private const string Host = "www.topmanhua.fan";
    private const int NavTimeoutMs = 30_000;
    private const int CaptureTimeoutMs = 45_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;

    /// <summary>
    /// Navigates to <paramref name="chapterUrl"/>, forces every lazy-loaded reading-content image
    /// to load, and returns whatever page bytes were captured off the network keyed by URL. A URL
    /// missing from the result simply never loaded in time — the caller falls back to a plain
    /// fetch for those rather than failing the whole chapter.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, byte[]>> FetchImagesAsync(
        string chapterUrl, IReadOnlyList<string> imageUrls, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var context = await EnsureContextAsync(ct);
                var page = await context.NewPageAsync();
                try
                {
                    return await CaptureAsync(page, chapterUrl, imageUrls, ct);
                }
                catch (ChallengeException) when (attempt == 0)
                {
                    logger.LogInformation("TopManhua browser hit a challenge; re-solving clearance and retrying");
                    fetcher.InvalidateSession(Host);
                    await ResetContextAsync();
                }
                finally
                {
                    await page.CloseAsync();
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyDictionary<string, byte[]>> CaptureAsync(
        IPage page, string chapterUrl, IReadOnlyList<string> imageUrls, CancellationToken ct)
    {
        var wanted = new HashSet<string>(imageUrls, StringComparer.Ordinal);
        var captured = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        page.Response += async (_, response) =>
        {
            if (!wanted.Contains(response.Url) || !response.Ok)
            {
                return;
            }

            try
            {
                captured[response.Url] = await response.BodyAsync();
            }
            catch (PlaywrightException)
            {
                // response body no longer available (e.g. page navigated away); skip it
            }
        };

        await page.GotoAsync(chapterUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = NavTimeoutMs });

        if (await ClassifyAsync(page) == PageVerdict.Challenge)
        {
            throw new ChallengeException();
        }

        // The site lazy-loads reading images (data-src, swapped in on scroll/intersection). Force
        // them all to load at once as native <img> fetches instead of scrolling through the page,
        // which is what makes this a genuine browser-issued image request (Referer, Sec-Fetch-*,
        // fingerprint) rather than a JS fetch() call.
        await page.EvaluateAsync(
            "document.querySelectorAll('.reading-content img[data-src]').forEach(img => { img.src = img.dataset.src; });");

        var deadline = DateTime.UtcNow.AddMilliseconds(CaptureTimeoutMs);
        while (captured.Count < wanted.Count && DateTime.UtcNow < deadline)
        {
            await page.WaitForTimeoutAsync(250);
        }

        if (captured.Count < wanted.Count)
        {
            logger.LogWarning(
                "TopManhua browser captured {Captured}/{Wanted} images for {Chapter}; missing ones will fall back to a plain fetch",
                captured.Count, wanted.Count, chapterUrl);
        }

        return captured;
    }

    private async Task<IBrowserContext> EnsureContextAsync(CancellationToken ct)
    {
        if (_context != null)
        {
            return _context;
        }

        var session = await fetcher.GetBrowserSessionAsync($"{BaseUrl}/", ct);

        _playwright ??= await Playwright.CreateAsync();
        _browser ??= await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            // ~100 MB headless shell — we never render headed, and it keeps the image far smaller.
            Channel = "chromium-headless-shell",
            Args = ["--disable-blink-features=AutomationControlled"],
        });

        var context = await _browser.NewContextAsync(new()
        {
            UserAgent = session.UserAgent,
            ViewportSize = new() { Width = 1280, Height = 2400 },
            // The headless shell advertises itself in the client hints ("HeadlessChrome") even
            // though the UA header is overridden above — restate them so they agree with the UA
            // FlareSolverr earned the clearance cookie with (see MangaFireBrowser for the same fix).
            ExtraHTTPHeaders = ClientHintsFor(session.UserAgent),
        });

        await context.AddInitScriptAsync("Object.defineProperty(navigator,'webdriver',{get:()=>undefined});");

        await context.AddCookiesAsync(session.Cookies.Select(c => new Cookie
        {
            Name = c.Key,
            Value = c.Value,
            Domain = $".{Host}",
            Path = "/",
        }).ToArray());

        _context = context;
        return _context;
    }

    /// <summary>Sec-CH-UA headers consistent with <paramref name="userAgent"/>, replacing the shell's own.</summary>
    private static Dictionary<string, string> ClientHintsFor(string userAgent)
    {
        var major = Regex.Match(userAgent, @"Chrome/(\d+)").Groups[1].Value;
        var platform = userAgent.Contains("Windows", StringComparison.Ordinal) ? "Windows"
            : userAgent.Contains("Macintosh", StringComparison.Ordinal) ? "macOS"
            : userAgent.Contains("Android", StringComparison.Ordinal) ? "Android"
            : "Linux";

        var headers = new Dictionary<string, string>
        {
            ["sec-ch-ua-mobile"] = "?0",
            ["sec-ch-ua-platform"] = $"\"{platform}\"",
        };

        if (major.Length > 0)
        {
            headers["sec-ch-ua"] = $"\"Chromium\";v=\"{major}\", \"Not_A Brand\";v=\"24\", \"Google Chrome\";v=\"{major}\"";
        }

        return headers;
    }

    private async Task ResetContextAsync()
    {
        if (_context != null)
        {
            await _context.CloseAsync();
            _context = null;
        }
    }

    private static readonly string[] BlockedTitleContains = ["Attention Required"];
    private static readonly string[] BlockedContentContains =
        ["you have been blocked", "used Cloudflare to restrict access"];

    private static Task<PageVerdict> ClassifyAsync(IPage page) =>
        CloudflareChallengeDetection.ClassifyAsync(page, BlockedTitleContains, BlockedContentContains);

    public async ValueTask DisposeAsync()
    {
        if (_context != null)
        {
            await _context.CloseAsync();
        }

        if (_browser != null)
        {
            await _browser.CloseAsync();
        }

        _playwright?.Dispose();
        _gate.Dispose();
    }
}
