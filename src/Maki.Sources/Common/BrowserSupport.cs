using System.Text.Json;
using System.Text.RegularExpressions;
using PuppeteerSharp;

namespace Maki.Sources.Common;

/// <summary>How a source's pages present themselves: what <c>BrowserSupport.PreparePageAsync</c> applies to each new page.</summary>
internal sealed record BrowserProfile(
    string UserAgent,
    int ViewportWidth,
    int ViewportHeight,
    IReadOnlyDictionary<string, string> ExtraHeaders,
    IReadOnlyList<string> InitScripts,
    Func<IRequest, bool> Abort);

internal static class BrowserSupport
{
    /// <summary>
    /// Launch flags both headless shells share. Without a GPU the shell still starts a GPU process
    /// running SwiftShader, 72 MB of it at launch; <c>--disable-software-rasterizer</c> stops that
    /// (12 MB), about 60 MB less at every stage. It also removes WebGL, which Cloudflare's bot
    /// script can read, so it was kept only after a live run against both sites showed no extra
    /// challenge or block.
    /// </summary>
    public static readonly string[] LaunchArgs =
    [
        "--disable-blink-features=AutomationControlled",
        "--disable-gpu",
        "--disable-software-rasterizer",
    ];

    private static readonly string[] DisabledFeatures =
    [
        "AvoidUnnecessaryBeforeUnloadCheckSync",
        "BoundaryEventDispatchTracksNodeRemoval",
        "DestroyProfileOnBrowserClose",
        "DialMediaRouteProvider",
        "GlobalMediaControls",
        "HttpsUpgrades",
        "LensOverlay",
        "MediaRouter",
        "PaintHolding",
        "ThirdPartyStoragePartitioning",
        "Translate",
        "AutoDeElevate",
        "RenderDocument",
        "OptimizationHints",
    ];

    /// <summary>
    /// Every flag the shell starts with: Playwright 1.61's own Chromium switches (less the Edge-only
    /// and screenshot ones), copied rather than Puppeteer's defaults, so the browser the sites see is
    /// the one they saw before. Puppeteer's
    /// defaults would add <c>--enable-automation</c> (which sets <c>navigator.webdriver</c>) and open
    /// a startup tab that costs a renderer. <c>--no-sandbox</c> because the sandbox needs user
    /// namespaces a container does not have; Playwright passed it too.
    /// </summary>
    public static readonly string[] ChromiumArgs =
    [
        "--disable-field-trial-config",
        "--disable-background-networking",
        "--disable-background-timer-throttling",
        "--disable-backgrounding-occluded-windows",
        "--disable-back-forward-cache",
        "--disable-breakpad",
        "--disable-client-side-phishing-detection",
        "--disable-component-extensions-with-background-pages",
        "--disable-component-update",
        "--no-default-browser-check",
        "--disable-default-apps",
        "--disable-dev-shm-usage",
        "--disable-extensions",
        "--disable-features=" + string.Join(',', DisabledFeatures),
        "--allow-pre-commit-input",
        "--disable-hang-monitor",
        "--disable-ipc-flooding-protection",
        "--disable-popup-blocking",
        "--disable-prompt-on-repost",
        "--disable-renderer-backgrounding",
        "--force-color-profile=srgb",
        "--metrics-recording-only",
        "--no-first-run",
        "--password-store=basic",
        "--use-mock-keychain",
        "--no-service-autorun",
        "--export-tagged-pdf",
        "--disable-search-engine-choice-screen",
        "--unsafely-disable-devtools-self-xss-warnings",
        "--disable-infobars",
        "--disable-sync",
        "--enable-unsafe-swiftshader",
        "--headless",
        "--hide-scrollbars",
        "--mute-audio",
        "--blink-settings=primaryHoverType=2,availableHoverTypes=2,primaryPointerType=4,availablePointerTypes=4",
        "--no-sandbox",
        "--no-startup-window",
        .. LaunchArgs,
    ];

    public const string HideWebDriver = "Object.defineProperty(navigator,'webdriver',{get:()=>undefined});";

    /// <summary>
    /// Analytics, share-button and web-font hosts the scrapers' pages load and nothing they read
    /// needs. Recorded from live MangaFire and TopManhua sessions on 2026-10-26. A denylist rather
    /// than an allowlist on purpose: an allowlist breaks the source the day a site moves its CDN.
    /// </summary>
    private static readonly string[] UnneededHosts =
    [
        "googletagmanager.com",
        "google-analytics.com",
        "sharethis.com",
        "amung.us",
        "fonts.googleapis.com",
        "fonts.gstatic.com",
    ];

    public static bool IsUnneeded(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        UnneededHosts.Any(h => uri.Host == h || uri.Host.EndsWith("." + h, StringComparison.Ordinal));

    public static bool IsImage(IRequest request) =>
        request.ResourceType is ResourceType.Image or ResourceType.Img or ResourceType.ImageSet;

    /// <summary>
    /// Sec-CH-UA headers consistent with <paramref name="userAgent"/>, replacing the headless shell's own.
    /// <paramref name="brandSecond"/> keeps TopManhua's original brand order, which its fingerprint was tuned on.
    /// </summary>
    public static Dictionary<string, string> ClientHintsFor(string userAgent, bool brandSecond = false)
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
            headers["sec-ch-ua"] = brandSecond
                ? $"\"Chromium\";v=\"{major}\", \"Not_A Brand\";v=\"24\", \"Google Chrome\";v=\"{major}\""
                : $"\"Chromium\";v=\"{major}\", \"Google Chrome\";v=\"{major}\", \"Not=A?Brand\";v=\"24\"";
        }

        return headers;
    }

    /// <summary>
    /// The <c>userAgentMetadata</c> Playwright sent with every user-agent override (its
    /// <c>calculateUserAgentMetadata</c>), so <c>navigator.userAgentData</c> keeps reading as it did.
    /// </summary>
    public static Dictionary<string, object> UserAgentMetadataFor(string userAgent)
    {
        var architecture = "x86";
        var platform = "Windows";
        var platformVersion = string.Empty;

        if (Regex.Match(userAgent, @"Android (\d+(\.\d+)?(\.\d+)?)") is { Success: true } android)
        {
            (platform, platformVersion, architecture) = ("Android", android.Groups[1].Value, "arm");
        }
        else if (Regex.Match(userAgent, @"iPhone OS (\d+(_\d+)?)") is { Success: true } iphone)
        {
            (platform, platformVersion, architecture) = ("iOS", iphone.Groups[1].Value, "arm");
        }
        else if (Regex.Match(userAgent, @"iPad; CPU OS (\d+(_\d+)?)") is { Success: true } ipad)
        {
            (platform, platformVersion, architecture) = ("iOS", ipad.Groups[1].Value, "arm");
        }
        else if (Regex.Match(userAgent, @"Mac OS X (\d+(_\d+)?(_\d+)?)") is { Success: true } mac)
        {
            (platform, platformVersion) = ("macOS", mac.Groups[1].Value);
            if (!userAgent.Contains("Intel", StringComparison.Ordinal))
            {
                architecture = "arm";
            }
        }
        else if (Regex.Match(userAgent, @"Windows\D+(\d+(\.\d+)?(\.\d+)?)") is { Success: true } windows)
        {
            (platform, platformVersion) = ("Windows", windows.Groups[1].Value);
        }
        else if (userAgent.Contains("linux", StringComparison.OrdinalIgnoreCase))
        {
            platform = "Linux";
        }

        if (userAgent.Contains("ARM", StringComparison.Ordinal))
        {
            architecture = "arm";
        }

        return new()
        {
            ["mobile"] = false,
            ["model"] = string.Empty,
            ["architecture"] = architecture,
            ["platform"] = platform,
            ["platformVersion"] = platformVersion,
        };
    }

    /// <summary>
    /// Applies a profile to a fresh page before its first navigation: what a Playwright context's
    /// options and init scripts used to do for every page it opened. The user agent, screen and window
    /// size go through the same DevTools calls Playwright made, so screen, window and viewport agree as
    /// they did there (a screen or window smaller than the viewport is a headless tell).
    /// </summary>
    public static async Task PreparePageAsync(IPage page, BrowserProfile profile)
    {
        await page.Client.SendAsync("Emulation.setUserAgentOverride", new Dictionary<string, object>
        {
            ["userAgent"] = profile.UserAgent,
            ["userAgentMetadata"] = UserAgentMetadataFor(profile.UserAgent),
        });
        await page.Client.SendAsync("Emulation.setDeviceMetricsOverride", new Dictionary<string, object>
        {
            ["mobile"] = false,
            ["width"] = profile.ViewportWidth,
            ["height"] = profile.ViewportHeight,
            ["screenWidth"] = profile.ViewportWidth,
            ["screenHeight"] = profile.ViewportHeight,
            ["deviceScaleFactor"] = 1,
            ["screenOrientation"] = new Dictionary<string, object> { ["angle"] = 0, ["type"] = "landscapePrimary" },
        });
        // The window too, or outerWidth stays at the shell's 800x600 under a larger viewport.
        var window = await page.Client.SendAsync<JsonElement>("Browser.getWindowForTarget");
        await page.Client.SendAsync("Browser.setWindowBounds", new Dictionary<string, object>
        {
            ["windowId"] = window.GetProperty("windowId").GetInt32(),
            ["bounds"] = new Dictionary<string, object> { ["width"] = profile.ViewportWidth, ["height"] = profile.ViewportHeight },
        });
        await page.SetExtraHttpHeadersAsync(new Dictionary<string, string>(profile.ExtraHeaders));
        foreach (var script in profile.InitScripts)
        {
            await page.EvaluateExpressionOnNewDocumentAsync(script);
        }

        // Playwright turned the cache off whenever a context routed requests, which is also what put
        // `pragma: no-cache` on every request the sites have seen from Maki so far.
        await page.SetCacheEnabledAsync(false);
        await page.SetRequestInterceptionAsync(true);
        page.Request += (_, e) => _ = RouteAsync(e.Request, profile.Abort);
    }

    private static async Task RouteAsync(IRequest request, Func<IRequest, bool> abort)
    {
        try
        {
            if (abort(request))
            {
                await request.AbortAsync();
            }
            else
            {
                await request.ContinueAsync();
            }
        }
        catch (Exception)
        {
            // the page closed under the request, or it was already handled; nothing left to route
        }
    }

    /// <summary>
    /// How long a response body may take once its headers are in. PuppeteerSharp's body read waits
    /// for the DevTools loading-finished event with no timeout of its own, and a Chromium that exits
    /// mid-response never sends it, which would hold the source's gate until a restart.
    /// </summary>
    public static readonly TimeSpan BodyTimeout = TimeSpan.FromSeconds(30);

    public static Task<string> ReadTextAsync(IResponse response, CancellationToken ct) =>
        ReadTextAsync(response, BodyTimeout, ct);

    internal static Task<string> ReadTextAsync(IResponse response, TimeSpan timeout, CancellationToken ct) =>
        response.TextAsync().WaitAsync(timeout, ct);

    public static Task<byte[]> ReadBytesAsync(IResponse response, CancellationToken ct) =>
        response.BufferAsync().AsTask().WaitAsync(BodyTimeout, ct);

    /// <summary>The cookies a FlareSolverr session earned, for a whole browser context.</summary>
    public static CookieData[] SessionCookies(IReadOnlyDictionary<string, string> cookies, string host) =>
        cookies.Select(c => new CookieData
        {
            Name = c.Key,
            Value = c.Value,
            Domain = $".{host}",
            Path = "/",
        }).ToArray();

    /// <summary>Closes a page or context that may already be gone; a dead process throws on close.</summary>
    public static Task CloseQuietlyAsync(IBrowserContext? context) =>
        CloseQuietlyAsync(context is null ? null : () => context.CloseAsync());

    public static Task CloseQuietlyAsync(IPage? page) =>
        CloseQuietlyAsync(page is null ? null : () => page.CloseAsync());

    private static async Task CloseQuietlyAsync(Func<Task>? close)
    {
        if (close is null)
        {
            return;
        }

        try
        {
            await close();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
    }
}
