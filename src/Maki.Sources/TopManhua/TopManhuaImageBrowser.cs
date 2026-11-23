using System.Collections.Concurrent;
using Maki.Core;
using Maki.Core.Http;
using Maki.Core.Http.Proxy;
using Maki.Sources.Common;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;

namespace Maki.Sources.TopManhua;

/// <summary>
/// Fetches a chapter's page images through a real browser instead of a plain HTTP request. The
/// image CDN (img-r2.2xstorage.com) sits behind Cloudflare bot management and blocks a bare
/// re-request for an image URL with a "Sorry, you have been blocked" page, even from a real
/// browser typed into the address bar, while the exact same image loads fine as an embedded
/// &lt;img&gt; on the chapter page. Headers alone don't close that gap, and neither does a bare
/// headless Chromium on its own: headless automation carries its own tells (WebGL renderer,
/// CDP artifacts, missing plugins) that Cloudflare's client-side bot-detection script picks up
/// regardless of what headers are sent. So this reuses <see cref="ChallengeAwareFetcher"/>'s
/// FlareSolverr-solved session the same way <c>MangaFireBrowser</c> does (FlareSolverr's browser
/// is specifically hardened against headless detection), then drives our own Chromium with that
/// session's cookies/UA to load the chapter page and let its native image pipeline fetch pages,
/// capturing the bytes off the network as they come in.
/// </summary>
public sealed class TopManhuaImageBrowser(
    ChallengeAwareFetcher fetcher,
    HeadlessBrowserLauncher launcher,
    ILogger<TopManhuaImageBrowser> logger) : IAsyncDisposable, IIdleBrowser
{
    private const string BaseUrl = "https://www.topmanhua.fan";
    private const string Host = "www.topmanhua.fan";
    private const string SourceName = "topmanhua";
    private const int NavTimeoutMs = 30_000;
    private const int CaptureTimeoutMs = 45_000;
    private const int MaxBodiesInFlight = 2;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private BrowserProfile? _profile;

    /// <summary>The route the current context was made with, and the clearance it holds was earned through.</summary>
    private ProxyRoute _route = ProxyRoute.Direct;
    private readonly IdleStamp _idle = new();

    public string BrowserName => "TopManhua";

    public bool IsRunning => _browser is not null;

    /// <summary>
    /// Closes the browser once nothing has scraped for
    /// <paramref name="idleFor"/>. See <see cref="IIdleBrowser"/> for what that is worth.
    /// </summary>
    public async Task<bool> ReleaseIfIdleAsync(TimeSpan idleFor)
    {
        if (_browser is null || (idleFor > TimeSpan.Zero && _idle.Idle < idleFor))
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
            if (_browser is null || (idleFor > TimeSpan.Zero && _idle.Idle < idleFor))
            {
                return false;
            }

            var idle = _idle.Idle;
            await ShutdownAsync();
            if (idleFor > TimeSpan.Zero)
            {
                logger.LogInformation(
                    "Closed the TopManhua browser after {Minutes:F0} idle minute(s); it relaunches on next use",
                    idle.TotalMinutes);
            }
            else
            {
                logger.LogInformation("Closed the TopManhua browser; it relaunches on next use");
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the context, then the browser process tree.</summary>
    private async Task ShutdownAsync()
    {
        var context = _context;
        var browser = _browser;
        _context = null;
        _browser = null;
        _profile = null;

        try
        {
            await BrowserSupport.CloseQuietlyAsync(context);
        }
        finally
        {
            await launcher.CloseAsync(browser);
        }
    }

    /// <summary>
    /// Navigates to <paramref name="chapterUrl"/>, forces every lazy-loaded reading-content image
    /// to load, and returns whatever page bytes were captured off the network keyed by URL. A URL
    /// missing from the result simply never loaded in time; the caller falls back to a plain
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
                var (context, profile) = await EnsureContextAsync(ct);
                IPage? opened = null;
                try
                {
                    opened = await context.NewPageAsync();
                    await BrowserSupport.PreparePageAsync(opened, profile);
                }
                catch (Exception ex)
                {
                    await BrowserSupport.CloseQuietlyAsync(opened);
                    if (attempt > 0 || ex is not PuppeteerException)
                    {
                        throw;
                    }

                    logger.LogWarning("TopManhua browser could not open a page; relaunching it");
                    await ShutdownAsync();
                    continue;
                }

                var page = opened;

                try
                {
                    return await CancellableBrowserCall.RunAsync(
                        () => BrowserSupport.CloseQuietlyAsync(page), () => CaptureAsync(page, chapterUrl, imageUrls, ct), ct);
                }
                catch (NavigationException ex) when (BrowserSupport.ProxyFailure(ex, _route, SourceName) is { } proxyFailure)
                {
                    throw proxyFailure;
                }
                catch (ChallengeException) when (attempt == 0)
                {
                    logger.LogInformation("TopManhua browser hit a challenge; re-solving clearance and retrying");
                    fetcher.InvalidateSession(Host, _route);
                    await ResetContextAsync();
                }
                finally
                {
                    await BrowserSupport.CloseQuietlyAsync(page);
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

        // Each body travels from Chromium as base64 JSON, so a 1 MB page is several MB of transient
        // garbage, and starting every capture of a 50-page chapter at once put all of it in flight
        // together. Not disposed: a late response can still be waiting on it
        // after the capture has returned.
        var inFlight = new SemaphoreSlim(MaxBodiesInFlight);

        async Task CaptureBodyAsync(IResponse response)
        {
            if (!wanted.Contains(response.Url) || !response.Ok)
            {
                return;
            }

            try
            {
                await inFlight.WaitAsync(ct);
                try
                {
                    captured[response.Url] = await BrowserSupport.ReadBytesAsync(response, ct);
                }
                finally
                {
                    inFlight.Release();
                }
            }
            catch (Exception)
            {
                // best-effort capture: the body may be gone (page closed or navigated away); skip it
            }
        }

        void OnResponse(object? sender, ResponseCreatedEventArgs e) => _ = CaptureBodyAsync(e.Response);

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
        await page.GoToAsync(chapterUrl, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
            Timeout = NavTimeoutMs,
            CancellationToken = ct,
        });

        if (await ClassifyAsync(page) == PageVerdict.Challenge)
        {
            throw new ChallengeException();
        }

        // The site lazy-loads reading images (data-src, swapped in on scroll/intersection). Force
        // them all to load at once as native <img> fetches instead of scrolling through the page,
        // which is what makes this a genuine browser-issued image request (Referer, Sec-Fetch-*,
        // fingerprint) rather than a JS fetch() call.
        await page.EvaluateExpressionAsync(
            "document.querySelectorAll('.reading-content img[data-src]').forEach(img => { img.src = img.dataset.src; });");

        var deadline = DateTime.UtcNow.AddMilliseconds(CaptureTimeoutMs);
        while (captured.Count < wanted.Count && DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(250, ct);
        }

        if (captured.Count < wanted.Count)
        {
            logger.LogWarning(
                "TopManhua browser captured {Captured}/{Wanted} images for {Chapter}; missing ones will fall back to a plain fetch",
                captured.Count, wanted.Count, chapterUrl);
        }

        return captured.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    private async Task<(IBrowserContext Context, BrowserProfile Profile)> EnsureContextAsync(CancellationToken ct)
    {
        if (_browser is { IsConnected: false })
        {
            logger.LogWarning("The TopManhua browser process is gone; relaunching it");
            await ShutdownAsync();
        }

        var route = await fetcher.RouteForAsync(SourceName, ct);
        if (_context != null && _profile != null)
        {
            if (route.Equals(_route))
            {
                return (_context, _profile);
            }

            logger.LogInformation("TopManhua's proxy setting changed; starting a new browser context");
            await ResetContextAsync();
        }

        var session = await fetcher.GetBrowserSessionAsync($"{BaseUrl}/", route, ct);

        _browser ??= await launcher.LaunchAsync(ct: ct);

        var context = await _browser.CreateBrowserContextAsync(BrowserSupport.ContextOptions(route));
        _route = route;
        await context.SetCookieAsync(BrowserSupport.SessionCookies(session.Cookies, Host));

        _profile = new BrowserProfile(
            session.UserAgent,
            1280,
            2400,
            // The headless shell advertises itself in the client hints ("HeadlessChrome") even
            // though the UA header is overridden; restate them so they agree with the UA
            // FlareSolverr earned the clearance cookie with (see MangaFireBrowser for the same fix).
            BrowserSupport.ClientHintsFor(session.UserAgent, brandSecond: true),
            [BrowserSupport.HideWebDriver],
            // Images are the point here, so they pass; fonts, media, frames and the tag manager are
            // renderer work for nothing. Verified live: every page image still arrives.
            request => request.ResourceType is ResourceType.Font or ResourceType.Media ||
                       (request.IsNavigationRequest && request.Frame?.ParentFrame is not null) ||
                       BrowserSupport.IsUnneeded(request.Url));

        _context = context;
        return (_context, _profile);
    }

    private async Task ResetContextAsync()
    {
        var context = _context;
        _context = null;
        _profile = null;
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
