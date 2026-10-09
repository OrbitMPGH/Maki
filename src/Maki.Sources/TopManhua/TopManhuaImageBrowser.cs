using System.Collections.Concurrent;
using Maki.Core;
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
    ILogger<TopManhuaImageBrowser> logger) : IAsyncDisposable, IIdleBrowser
{
    private const string BaseUrl = "https://www.topmanhua.fan";
    private const string Host = "www.topmanhua.fan";
    private const int NavTimeoutMs = 30_000;
    private const int CaptureTimeoutMs = 45_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private readonly IdleStamp _idle = new();

    public string BrowserName => "TopManhua";

    public bool IsRunning => _playwright is not null;

    /// <summary>
    /// Closes the browser and the driver process behind it once nothing has scraped for
    /// <paramref name="idleFor"/>. See <see cref="IIdleBrowser"/> for what that is worth.
    /// </summary>
    public async Task<bool> ReleaseIfIdleAsync(TimeSpan idleFor)
    {
        if (_playwright is null || _idle.Idle < idleFor)
        {
            return false;
        }

        // Zero timeout on purpose. The gate is held for the whole of a scrape, so failing to take
        // it means one is running and this is not an idle browser after all; there is nothing to
        // wait for, the next pass will find it idle.
        if (!await _gate.WaitAsync(0))
        {
            return false;
        }

        try
        {
            if (_playwright is null || _idle.Idle < idleFor)
            {
                return false;
            }

            var idle = _idle.Idle;
            await ShutdownAsync();
            logger.LogInformation(
                "Closed the TopManhua browser after {Minutes:F0} idle minute(s); it relaunches on next use",
                idle.TotalMinutes);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Tears the whole stack down, in order. Disposing the driver is the part that matters most:
    /// it is what ends the Node process, which is the larger half of what a parked browser costs.
    /// </summary>
    private async Task ShutdownAsync()
    {
        var context = _context;
        var browser = _browser;
        var playwright = _playwright;
        _context = null;
        _browser = null;
        _playwright = null;

        await BrowserSupport.CloseQuietlyAsync(context);
        await BrowserSupport.CloseQuietlyAsync(browser);
        playwright?.Dispose();
    }

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
        _idle.Touch();
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var context = await EnsureContextAsync(ct);
                IPage page;
                try
                {
                    page = await context.NewPageAsync();
                }
                catch (PlaywrightException) when (attempt == 0)
                {
                    logger.LogWarning("TopManhua browser could not open a page; relaunching it");
                    await ShutdownAsync();
                    continue;
                }

                try
                {
                    return await CancellableBrowserCall.RunAsync(
                        () => page.CloseAsync(), () => CaptureAsync(page, chapterUrl, imageUrls, ct), ct);
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
                    _idle.Touch();
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
        var captured = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);

        async Task CaptureBodyAsync(IResponse response)
        {
            if (!wanted.Contains(response.Url) || !response.Ok)
            {
                return;
            }

            try
            {
                captured[response.Url] = await response.BodyAsync();
            }
            catch (Exception)
            {
                // best-effort capture: the body may be gone (page closed or navigated away); skip it
            }
        }

        void OnResponse(object? sender, IResponse response) => _ = CaptureBodyAsync(response);

        page.Response += OnResponse;
        try
        {
            return await NavigateAndCaptureAsync(page, chapterUrl, wanted, captured, ct);
        }
        finally
        {
            page.Response -= OnResponse;
        }
    }

    private async Task<IReadOnlyDictionary<string, byte[]>> NavigateAndCaptureAsync(
        IPage page, string chapterUrl, HashSet<string> wanted, ConcurrentDictionary<string, byte[]> captured,
        CancellationToken ct)
    {
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
            ct.ThrowIfCancellationRequested();
            await page.WaitForTimeoutAsync(250);
        }

        if (captured.Count < wanted.Count)
        {
            logger.LogWarning(
                "TopManhua browser captured {Captured}/{Wanted} images for {Chapter}; missing ones will fall back to a plain fetch",
                captured.Count, wanted.Count, chapterUrl);
        }

        return captured.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    private async Task<IBrowserContext> EnsureContextAsync(CancellationToken ct)
    {
        if (_browser is { IsConnected: false })
        {
            logger.LogWarning("The TopManhua browser process is gone; relaunching it");
            await ShutdownAsync();
        }

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
            ExtraHTTPHeaders = BrowserSupport.ClientHintsFor(session.UserAgent, brandSecond: true),
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

    private async Task ResetContextAsync()
    {
        var context = _context;
        _context = null;
        await BrowserSupport.CloseQuietlyAsync(context);
    }

    private static readonly string[] BlockedTitleContains = ["Attention Required"];
    private static readonly string[] BlockedContentContains =
        ["you have been blocked", "used Cloudflare to restrict access"];

    private static Task<PageVerdict> ClassifyAsync(IPage page) =>
        CloudflareChallengeDetection.ClassifyAsync(page, BlockedTitleContains, BlockedContentContains);

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
        _gate.Dispose();
    }
}
