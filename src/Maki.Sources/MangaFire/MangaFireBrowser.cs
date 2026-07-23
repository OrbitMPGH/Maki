using System.Text.Json;
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
    /// the chapter pager. Only the language the site loads (the site default, English) is supported;
    /// a different requested language throws until dropdown-driven switching is validated against a
    /// multi-language series.
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

                var loadedLanguage = QueryParam(firstResponse.Url, "language") ?? "en";
                if (!string.IsNullOrWhiteSpace(language) &&
                    !language.Equals(loadedLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    throw new NotSupportedException(
                        $"MangaFire browser scraping currently serves only the site-default language " +
                        $"('{loadedLanguage}'); '{language}' would need dropdown-driven switching.");
                }

                // let the chapter list + pager paint and bring them on-screen before driving them
                try
                {
                    await page.Locator(".title-detail__chapters").ScrollIntoViewIfNeededAsync(new() { Timeout = 8000 });
                }
                catch (PlaywrightException)
                {
                    // pager may already be in view
                }

                await page.WaitForTimeoutAsync(500);

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
            var launch = new BrowserTypeLaunchOptions { Headless = true, Channel = "chromium-headless-shell" };
            var resolverRules = await settings.GetAsync(SettingKeys.MangaFireBrowserHostResolverRules, ct);
            if (!string.IsNullOrWhiteSpace(resolverRules))
            {
                launch.Args = [$"--host-resolver-rules={resolverRules}"];
            }

            _browser = await _playwright.Chromium.LaunchAsync(launch);
        }

        var context = await _browser.NewContextAsync(new()
        {
            UserAgent = session.UserAgent,
            ViewportSize = new() { Width = 1280, Height = 900 },
        });

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

    private async Task ResetContextAsync()
    {
        if (_context != null)
        {
            await _context.CloseAsync();
            _context = null;
        }
    }

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
            if (await LooksLikeChallengeAsync(page))
            {
                throw new ChallengeException(
                    "MangaFire: the headless browser did not clear Cloudflare — the FlareSolverr clearance " +
                    "cookie was rejected. This usually means Maki's network egress IP differs from " +
                    "FlareSolverr's (the cookie is IP-bound); run both behind the same egress or proxy.");
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

    private static async Task<bool> LooksLikeChallengeAsync(IPage page)
    {
        try
        {
            if ((await page.TitleAsync()).Contains("Just a moment", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var content = await page.ContentAsync();
            return content.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase)
                || content.Contains("cf-challenge", StringComparison.OrdinalIgnoreCase)
                || content.Contains("__cf_chl", StringComparison.OrdinalIgnoreCase)
                || content.Contains("Just a moment", StringComparison.OrdinalIgnoreCase);
        }
        catch (PlaywrightException)
        {
            return false;
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
