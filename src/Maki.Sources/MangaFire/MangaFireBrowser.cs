using System.Text.Json;
using System.Text.RegularExpressions;
using Maki.Core.Configuration;
using Maki.Core.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace Maki.Sources.MangaFire;

/// <summary>
/// Drives a shared headless Chromium to defeat MangaFire's client-side request signature.
///
/// The site's SPA signs every protected <c>/api</c> call with a <c>vrf</c> query token minted by an
/// obfuscated, anti-tamper "protection" module (seeded by a per-build <c>window.__config</c>). The
/// token binds the request's headers, the module actively resists observation/instrumentation, and
/// it issues requests through a network primitive our page hooks can't reach — so the token can be
/// neither reverse-implemented in C# nor forged from an injected <c>fetch</c>. The only durable way
/// in is to let the site's own code sign and issue the request inside a real browser and read the
/// response back off the network. That is what this class does, matching how upstream Tachiyomi
/// extensions solve it (a WebView).
///
/// Cloudflare clearance is reused from <see cref="ChallengeAwareFetcher"/> (FlareSolverr already
/// solves it), so Chromium starts past the challenge. All calls are serialised through one context;
/// image/media/font loads are aborted since only the JSON responses matter.
/// </summary>
public sealed class MangaFireBrowser(
    ChallengeAwareFetcher fetcher,
    IAppSettings settings,
    ILogger<MangaFireBrowser> logger) : IAsyncDisposable
{
    private const string BaseUrl = "https://mangafire.to";
    private const string Host = "mangafire.to";
    private const int NavTimeoutMs = 45_000;
    private const int ResponseTimeoutMs = 30_000;
    private const int PageResponseTimeoutMs = 12_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;

    /// <summary>Search-results JSON for a keyword (the <c>/api/titles?keyword=…</c> payload).</summary>
    public Task<string> SearchAsync(string keyword, CancellationToken ct) =>
        CaptureAsync(
            $"{BaseUrl}/browse?keyword={Uri.EscapeDataString(keyword)}",
            url => url.Contains("/api/titles?", StringComparison.Ordinal),
            ct);

    /// <summary>Series-detail JSON (the <c>/api/titles/{hid}</c> payload).</summary>
    public Task<string> SeriesAsync(string seriesId, CancellationToken ct)
    {
        var hid = HidFrom(seriesId);
        return CaptureAsync(
            $"{BaseUrl}/title/{seriesId}",
            url => IsDetailUrl(url, hid),
            ct);
    }

    /// <summary>Chapter-pages JSON (the <c>/api/chapters/{id}</c> payload) via a reader navigation.</summary>
    public Task<string> PagesAsync(string seriesId, string chapterId, CancellationToken ct) =>
        CaptureAsync(
            $"{BaseUrl}/title/{seriesId}/chapter/{chapterId}",
            url => url.Contains($"/api/chapters/{chapterId}", StringComparison.Ordinal),
            ct);

    /// <summary>
    /// Every chapter item (raw JSON) for a language, gathered by loading the title page and walking
    /// the chapter pager. The title page loads whichever language the site defaults to for that
    /// series (not always English — a Japanese-only title defaults to <c>ja</c>), so when the request
    /// asks for a different one the "Lang" dropdown is driven: the matching option when the title has
    /// it, otherwise "All" (which returns every language, each item carrying its own <c>language</c>
    /// code for the caller to filter on). A title with no chapters in the requested language simply
    /// yields nothing.
    /// </summary>
    public async Task<IReadOnlyList<string>> ChaptersAsync(string seriesId, string language, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await WithPageAsync(async page =>
            {
                var items = new Dictionary<long, string>();
                int? lastPage = null;

                async Task CollectAsync(IResponse response)
                {
                    if (!IsChaptersUrl(response.Url))
                    {
                        return;
                    }

                    if (response.Status == 403)
                    {
                        throw new ChallengeException();
                    }

                    using var doc = JsonDocument.Parse(await response.TextAsync());
                    var root = doc.RootElement;
                    if (root.TryGetProperty("items", out var arr))
                    {
                        foreach (var item in arr.EnumerateArray())
                        {
                            if (item.TryGetProperty("id", out var id))
                            {
                                items[id.GetInt64()] = item.GetRawText();
                            }
                        }
                    }

                    if (root.TryGetProperty("meta", out var meta) && meta.TryGetProperty("lastPage", out var lp))
                    {
                        lastPage = lp.GetInt32();
                    }
                }

                var firstWait = page.WaitForResponseAsync(r => IsChaptersUrl(r.Url), new() { Timeout = ResponseTimeoutMs });
                var firstResponse = await NavigateAndCaptureAsync(page, firstWait, $"{BaseUrl}/title/{seriesId}");
                await CollectAsync(firstResponse);

                // let the chapter list + toolbar paint and bring them on-screen before driving them
                try
                {
                    await page.Locator(".title-detail__chapters").ScrollIntoViewIfNeededAsync(new() { Timeout = 8000 });
                }
                catch (PlaywrightException)
                {
                    // list may already be in view
                }

                await page.WaitForTimeoutAsync(500);

                var loadedLanguage = QueryParam(firstResponse.Url, "language") ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(language) &&
                    !language.Equals(loadedLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    var switched = await SwitchLanguageAsync(page, language, loadedLanguage);
                    if (switched == null)
                    {
                        logger.LogInformation(
                            "MangaFire {Series}: could not switch from '{Loaded}' to '{Wanted}'; keeping the loaded list",
                            seriesId, loadedLanguage, language);
                    }
                    else
                    {
                        // the list was replaced wholesale — the previous language's items don't belong
                        items.Clear();
                        lastPage = null;
                        await CollectAsync(switched);
                    }
                }

                var expected = lastPage ?? 1;
                for (var pageNo = 2; pageNo <= expected; pageNo++)
                {
                    var wait = page.WaitForResponseAsync(
                        r => IsChaptersUrl(r.Url) && r.Url.Contains($"page={pageNo}", StringComparison.Ordinal),
                        new() { Timeout = PageResponseTimeoutMs });

                    if (!await ClickNextPageAsync(page, pageNo))
                    {
                        logger.LogWarning("MangaFire pager stalled at page {Page}/{Last} for {Series}", pageNo, expected, seriesId);
                        break;
                    }

                    await CollectAsync(await wait);
                }

                return (IReadOnlyList<string>)items.Values.ToList();
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Navigate <paramref name="navUrl"/> and return the first response body whose URL matches.</summary>
    private async Task<string> CaptureAsync(string navUrl, Func<string, bool> matches, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await WithPageAsync(async page =>
            {
                var wait = page.WaitForResponseAsync(r => matches(r.Url), new() { Timeout = ResponseTimeoutMs });
                var response = await NavigateAndCaptureAsync(page, wait, navUrl);
                return await response.TextAsync();
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs <paramref name="action"/> on a fresh page; on a 403/challenge, re-solves once and retries.</summary>
    private async Task<T> WithPageAsync<T>(Func<IPage, Task<T>> action, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var context = await EnsureContextAsync(ct);
            var page = await context.NewPageAsync();
            try
            {
                return await action(page);
            }
            catch (ChallengeException) when (attempt == 0)
            {
                logger.LogInformation("MangaFire browser hit a challenge; re-solving clearance and retrying");
                fetcher.InvalidateSession(Host);
                await ResetContextAsync();
            }
            finally
            {
                await page.CloseAsync();
            }
        }
    }

    private async Task<IBrowserContext> EnsureContextAsync(CancellationToken ct)
    {
        if (_context != null)
        {
            return _context;
        }

        var session = await fetcher.GetBrowserSessionAsync($"{BaseUrl}/home", ct);

        _playwright ??= await Playwright.CreateAsync();
        if (_browser == null)
        {
            // Use the ~100 MB headless shell, not full Chromium — we never render headed, and it
            // keeps the Docker image far smaller. The Dockerfile installs only this browser.
            var args = new List<string> { "--disable-blink-features=AutomationControlled" };
            var resolverRules = await settings.GetAsync(SettingKeys.MangaFireBrowserHostResolverRules, ct);
            if (!string.IsNullOrWhiteSpace(resolverRules))
            {
                args.Add($"--host-resolver-rules={resolverRules}");
            }

            var launch = new BrowserTypeLaunchOptions
            {
                Headless = true,
                Channel = "chromium-headless-shell",
                Args = args,
            };

            _browser = await _playwright.Chromium.LaunchAsync(launch);
        }

        var context = await _browser.NewContextAsync(new()
        {
            UserAgent = session.UserAgent,
            ViewportSize = new() { Width = 1280, Height = 900 },
            // The headless shell advertises itself in the client hints ("HeadlessChrome") even though
            // the UA header is overridden above — the mismatch between the two is a decisive bot signal,
            // and Cloudflare answers it with an outright "Access denied" block (not a solvable challenge)
            // wherever the egress IP's reputation is anything short of pristine. Restate the hints so
            // they agree with the UA FlareSolverr earned the clearance cookie with.
            ExtraHTTPHeaders = ClientHintsFor(session.UserAgent),
        });

        await context.AddInitScriptAsync("Object.defineProperty(navigator,'webdriver',{get:()=>undefined});");

        // The site remembers the last chapter-list language/filter in web storage, and the context is
        // shared across every series we scrape — so a title would otherwise load carrying the previous
        // title's selection, which desynchronises the pager mid-walk. Start every navigation clean.
        await context.AddInitScriptAsync("try { localStorage.clear(); sessionStorage.clear(); } catch (e) { }");

        // only the JSON responses matter — skip images/media/fonts to cut nav time and bandwidth.
        await context.RouteAsync("**/*", route =>
        {
            var type = route.Request.ResourceType;
            if (type is "image" or "media" or "font")
            {
                _ = route.AbortAsync();
            }
            else
            {
                _ = route.ContinueAsync();
            }
        });

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
            headers["sec-ch-ua"] = $"\"Chromium\";v=\"{major}\", \"Google Chrome\";v=\"{major}\", \"Not=A?Brand\";v=\"24\"";
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

    /// <summary>
    /// MangaFire language codes to the label its "Lang" dropdown shows (minus the flag emoji).
    /// Only the codes seen in the wild are certain (en, es-la, pt-br); the rest follow the same
    /// English-name convention and cost nothing when wrong — an unmatched code falls back to "All".
    /// </summary>
    private static readonly Dictionary<string, string> LanguageLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "English",
        ["ja"] = "Japanese",
        ["fr"] = "French",
        ["de"] = "German",
        ["it"] = "Italian",
        ["es"] = "Spanish",
        ["es-la"] = "Spanish (LATAM)",
        ["pt"] = "Portuguese",
        ["pt-br"] = "Portuguese (Br)",
        ["zh"] = "Chinese",
        ["ko"] = "Korean",
        ["ru"] = "Russian",
        ["ar"] = "Arabic",
        ["id"] = "Indonesian",
        ["th"] = "Thai",
        ["vi"] = "Vietnamese",
        ["pl"] = "Polish",
        ["tr"] = "Turkish",
    };

    /// <summary>
    /// Drives the "Lang" dropdown to <paramref name="wanted"/>, falling back to "All" when the title
    /// doesn't offer that language (the menu only lists languages the title actually has). Returns the
    /// first chapters response of the new selection, or null if nothing could be selected.
    /// </summary>
    private static async Task<IResponse?> SwitchLanguageAsync(IPage page, string wanted, string loaded)
    {
        var menu = page.Locator("button.select", new() { HasText = "Lang" }).First;
        try
        {
            if (await menu.CountAsync() == 0)
            {
                return null;
            }

            await menu.DispatchEventAsync("click");
        }
        catch (PlaywrightException)
        {
            return null;
        }

        await page.WaitForTimeoutAsync(300);

        var wait = page.WaitForResponseAsync(
            r => IsChaptersUrl(r.Url) &&
                 !string.Equals(QueryParam(r.Url, "language") ?? string.Empty, loaded, StringComparison.OrdinalIgnoreCase),
            new() { Timeout = PageResponseTimeoutMs });

        var label = LanguageLabels.GetValueOrDefault(wanted);
        if ((label == null || !await ClickMenuItemAsync(page, label)) && !await ClickMenuItemAsync(page, "All"))
        {
            return null;
        }

        try
        {
            return await wait;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>Clicks the open dropdown's item whose label (flag emoji stripped) equals <paramref name="label"/>.</summary>
    private static async Task<bool> ClickMenuItemAsync(IPage page, string label) =>
        await page.EvaluateAsync<bool>(
            """
            (label) => {
              const norm = s => (s || '').replace(/[^A-Za-z()\s-]/g, '').replace(/\s+/g, ' ').trim().toLowerCase();
              const items = [...document.querySelectorAll('.dropdown__menu button, .dropdown__menu [role=menuitem]')];
              const target = items.find(e => norm(e.textContent) === norm(label));
              if (!target) return false;
              target.dispatchEvent(new MouseEvent('click', { bubbles: true }));
              return true;
            }
            """, label);

    /// <summary>
    /// Advances the chapter pager to <paramref name="pageNo"/>. Uses the "Next page" arrow while it's
    /// present, falling back to the numbered button once the arrow drops out of the final window.
    /// Fires the click as a dispatched event because the pager re-renders (shifting its number window)
    /// and a normal actionable click races the detach.
    /// </summary>
    private static async Task<bool> ClickNextPageAsync(IPage page, int pageNo)
    {
        ILocator[] candidates =
        [
            page.Locator("[class*=pager] button[aria-label='Next page']"),
            page.GetByRole(AriaRole.Button, new() { Name = "Next page" }),
            page.Locator($"[class*=pager] button:text-is('{pageNo}')"),
        ];

        // The pager is briefly absent mid-re-render, so a single sweep of the candidates can find
        // nothing clickable on a page that is perfectly reachable a moment later — sweep again.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                await page.WaitForTimeoutAsync(400);
            }

            try
            {
                await page.Locator("[class*=pager]").First.ScrollIntoViewIfNeededAsync(new() { Timeout = 3000 });
            }
            catch (PlaywrightException)
            {
                // pager may be mid-render; the candidate sweep below reports the real outcome
            }

            foreach (var candidate in candidates)
            {
                try
                {
                    var element = candidate.First;
                    if (await element.CountAsync() == 0 || !await element.IsVisibleAsync() || !await element.IsEnabledAsync())
                    {
                        continue;
                    }

                    await element.DispatchEventAsync("click");
                    return true;
                }
                catch (PlaywrightException)
                {
                    // try the next candidate
                }
            }
        }

        return false;
    }

    private static bool IsChaptersUrl(string url) =>
        url.Contains("/api/titles/", StringComparison.Ordinal) &&
        url.Contains("/chapters", StringComparison.Ordinal);

    private static bool IsDetailUrl(string url, string hid) =>
        url.Contains($"/api/titles/{hid}", StringComparison.Ordinal) &&
        !url.Contains("/chapters", StringComparison.Ordinal) &&
        !url.Contains("/volumes", StringComparison.Ordinal);

    private static string? QueryParam(string url, string key)
    {
        foreach (var pair in new Uri(url).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == key)
            {
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        return null;
    }

    private static string HidFrom(string seriesId)
    {
        var dash = seriesId.IndexOf('-');
        return dash > 0 ? seriesId[..dash] : seriesId;
    }

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

    /// <summary>Navigate, then await the pre-armed response wait, turning an opaque timeout into a
    /// diagnosable cause (Cloudflare challenge vs. a page that simply never issued the request).</summary>
    private async Task<IResponse> NavigateAndCaptureAsync(IPage page, Task<IResponse> waitTask, string navUrl)
    {
        await page.GotoAsync(navUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = NavTimeoutMs });

        IResponse response;
        try
        {
            response = await waitTask;
        }
        catch (TimeoutException)
        {
            switch (await ClassifyAsync(page))
            {
                case PageVerdict.Challenge:
                    throw new ChallengeException(
                        "MangaFire: the headless browser did not clear Cloudflare — the FlareSolverr clearance " +
                        "cookie was rejected. This usually means Maki's network egress IP differs from " +
                        "FlareSolverr's (the cookie is IP-bound); run both behind the same egress or proxy.");
                case PageVerdict.Blocked:
                    throw new ChallengeException(
                        "MangaFire: Cloudflare served an 'Access denied' block page — this is a firewall/bot-score " +
                        "rejection, not a solvable challenge, so re-solving won't help on its own. It's driven by the " +
                        "egress IP's reputation (VPN/VPS ranges score badly) plus the headless browser's fingerprint; " +
                        "route Maki's traffic through a residential-grade egress if it persists.");
            }

            var title = await SafeTitleAsync(page);
            throw new InvalidOperationException(
                $"MangaFire: '{navUrl}' loaded but the expected API request never fired " +
                $"(page title '{title}', url {page.Url}).");
        }

        if (response.Status == 403)
        {
            throw new ChallengeException();
        }

        return response;
    }

    private enum PageVerdict
    {
        /// <summary>Not obviously Cloudflare's doing.</summary>
        Unknown,

        /// <summary>The solvable JS interstitial ("Just a moment") — stale clearance, worth re-solving.</summary>
        Challenge,

        /// <summary>A firewall/bot-score block ("Access denied", error 1020) — re-solving alone won't clear it.</summary>
        Blocked,
    }

    private static async Task<PageVerdict> ClassifyAsync(IPage page)
    {
        try
        {
            var title = await page.TitleAsync();
            var content = await page.ContentAsync();

            if (title.Contains("Access denied", StringComparison.OrdinalIgnoreCase)
                || title.Contains("Attention Required", StringComparison.OrdinalIgnoreCase)
                || content.Contains("used Cloudflare to restrict access", StringComparison.OrdinalIgnoreCase)
                || content.Contains("Error code 1020", StringComparison.OrdinalIgnoreCase)
                || content.Contains("error code: 1020", StringComparison.OrdinalIgnoreCase))
            {
                return PageVerdict.Blocked;
            }

            if (title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
                || content.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase)
                || content.Contains("cf-challenge", StringComparison.OrdinalIgnoreCase)
                || content.Contains("__cf_chl", StringComparison.OrdinalIgnoreCase)
                || content.Contains("Just a moment", StringComparison.OrdinalIgnoreCase))
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

    private static async Task<string> SafeTitleAsync(IPage page)
    {
        try
        {
            return await page.TitleAsync();
        }
        catch (PlaywrightException)
        {
            return "(unavailable)";
        }
    }

    /// <summary>Raised when a captured response comes back as a Cloudflare 403 (or the page is stuck on a
    /// challenge), to trigger a re-solve and retry.</summary>
    private sealed class ChallengeException(string? message = null) : Exception(message);
}
