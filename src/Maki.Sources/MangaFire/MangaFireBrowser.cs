using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Maki.Core;
using Maki.Core.Configuration;
using Maki.Core.Http.Proxy;
using Maki.Core.Http;
using Maki.Sources.Common;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;

namespace Maki.Sources.MangaFire;

/// <summary>
/// Drives a shared headless Chromium to defeat MangaFire's client-side request signature.
///
/// The site's SPA signs every protected <c>/api</c> call with a <c>vrf</c> query token minted by an
/// obfuscated, anti-tamper "protection" module (seeded by a per-build <c>window.__config</c>). The
/// token binds the request's headers, the module actively resists observation/instrumentation, and
/// it issues requests through a network primitive our page hooks can't reach, so the token can be
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
    HeadlessBrowserLauncher launcher,
    ILogger<MangaFireBrowser> logger) : IAsyncDisposable, IIdleBrowser
{
    private const string BaseUrl = "https://mangafire.to";
    private const string Host = "mangafire.to";
    private const string SourceName = "mangafire";
    private const int NavTimeoutMs = 45_000;
    private const int ResponseTimeoutMs = 30_000;
    private const int PageResponseTimeoutMs = 12_000;
    private const int TrackerRowTimeoutMs = 8_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private BrowserProfile? _profile;

    /// <summary>The route the current context was made with, and the clearance it holds was earned through.</summary>
    private ProxyRoute _route = ProxyRoute.Direct;
    private readonly IdleStamp _idle = new();

    public string BrowserName => "MangaFire";

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
                    "Closed the MangaFire browser after {Minutes:F0} idle minute(s); it relaunches on next use",
                    idle.TotalMinutes);
            }
            else
            {
                logger.LogInformation("Closed the MangaFire browser; it relaunches on next use");
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

    /// <summary>Search-results JSON for a keyword (the <c>/api/titles?keyword=…</c> payload).</summary>
    public Task<string> SearchAsync(string keyword, CancellationToken ct) =>
        CaptureAsync(
            $"{BaseUrl}/browse?keyword={Uri.EscapeDataString(keyword)}",
            IsSearchUrl,
            ct);

    /// <summary>
    /// The browse page's result call. Matched on the keyword parameter as well as the known path so a
    /// rename of the endpoint (it has moved before) doesn't turn every search into a 30-second timeout;
    /// nothing else on that page carries <c>keyword=</c>.
    /// </summary>
    private static bool IsSearchUrl(string url) =>
        url.Contains("/api/", StringComparison.Ordinal) &&
        (url.Contains("/api/titles?", StringComparison.Ordinal) ||
         url.Contains("keyword=", StringComparison.Ordinal));

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
    /// Pass as <c>language</c> to drive the dropdown to "All", the mixed view where every item
    /// carries its own <c>language</c> field. It is not a MangaFire language code, which is why it
    /// is absent from <see cref="LanguageLabels"/>: the lookup missing is exactly what routes it to
    /// the "All" item that <see cref="SwitchLanguageAsync"/> already falls back to.
    /// </summary>
    public const string AllLanguages = "all";

    /// <summary>
    /// Every chapter item (raw JSON) for a language, gathered by loading the title page and walking
    /// the chapter pager. The title page loads whichever language the site defaults to for that
    /// series (not always English: a Japanese-only title defaults to <c>ja</c>), so when the request
    /// asks for a different one the "Lang" dropdown is driven: the matching option when the title has
    /// it, otherwise "All" (which returns every language, each item carrying its own <c>language</c>
    /// code for the caller to filter on). A title with no chapters in the requested language simply
    /// yields nothing.
    /// <para>
    /// Returns the chapter list items, plus whether the returned list is confirmed to be in <paramref
    /// name="language"/>: either it was already loaded in that language, or a switch to it
    /// succeeded. When false (a switch was attempted and failed), the caller is looking at
    /// whatever language was loaded before the attempt, not the one it asked for, so an unlabelled
    /// item here must not be assumed to match the request.
    /// </para>
    /// <para>
    /// <paramref name="stopAt"/> is shown each page's items as it arrives (newest first) and ends the
    /// walk when it returns true. <c>Complete</c> is false only when that left pages unread.
    /// </para>
    /// </summary>
    public async Task<(IReadOnlyList<string> Items, bool LanguageMatched, bool Complete)> ChaptersAsync(
        string seriesId, string language, CancellationToken ct, Func<IReadOnlyList<string>, bool>? stopAt = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await WithPageAsync(async page =>
            {
                var items = new Dictionary<long, string>();
                var pageItems = new List<string>();
                int? lastPage = null;

                async Task CollectAsync(IResponse response)
                {
                    pageItems.Clear();
                    if (!IsChaptersUrl(response.Url))
                    {
                        return;
                    }

                    if (response.Status == HttpStatusCode.Forbidden)
                    {
                        throw new ChallengeException();
                    }

                    using var doc = JsonDocument.Parse(await BrowserSupport.ReadTextAsync(response, ct));
                    var root = doc.RootElement;
                    if (root.TryGetProperty("items", out var arr))
                    {
                        foreach (var item in arr.EnumerateArray())
                        {
                            if (item.TryGetProperty("id", out var id))
                            {
                                var text = item.GetRawText();
                                items[id.GetInt64()] = text;
                                pageItems.Add(text);
                            }
                        }
                    }

                    if (root.TryGetProperty("meta", out var meta) && meta.TryGetProperty("lastPage", out var lp))
                    {
                        lastPage = lp.GetInt32();
                    }
                }

                var firstWait = page.WaitForResponseAsync(r => IsChaptersUrl(r.Url), Wait(ResponseTimeoutMs, ct));
                var firstResponse = await NavigateAndCaptureAsync(page, firstWait, $"{BaseUrl}/title/{seriesId}", ct);
                await CollectAsync(firstResponse);

                // let the chapter list + toolbar paint and bring them on-screen before driving them
                await ScrollIntoViewAsync(page, ".title-detail__chapters", 8000, ct);

                await Task.Delay(500, ct);

                var loadedLanguage = QueryParam(firstResponse.Url, "language") ?? string.Empty;
                // An empty language on the loaded list already *is* the "All" view, so asking to
                // switch to it would wait for a response that never comes and time out.
                var alreadyLoaded = language.Equals(loadedLanguage, StringComparison.OrdinalIgnoreCase) ||
                    (language.Equals(AllLanguages, StringComparison.OrdinalIgnoreCase) && loadedLanguage.Length == 0);
                var languageMatched = alreadyLoaded;
                if (!string.IsNullOrWhiteSpace(language) && !alreadyLoaded)
                {
                    var switched = await SwitchLanguageAsync(page, language, loadedLanguage, ct);
                    if (switched == null)
                    {
                        logger.LogInformation(
                            "MangaFire {Series}: could not switch from '{Loaded}' to '{Wanted}'; keeping the loaded list",
                            seriesId, loadedLanguage, language);
                    }
                    else
                    {
                        // the list was replaced wholesale; the previous language's items don't belong
                        items.Clear();
                        lastPage = null;
                        await CollectAsync(switched);
                        languageMatched = true;
                    }
                }

                var expected = lastPage ?? 1;
                for (var pageNo = 2; pageNo <= expected; pageNo++)
                {
                    if (stopAt?.Invoke(pageItems) == true)
                    {
                        logger.LogDebug("MangaFire {Series}: stopped at page {Page} of {Last}, nothing new past it",
                            seriesId, pageNo - 1, expected);
                        return ((IReadOnlyList<string>)items.Values.ToList(), languageMatched, false);
                    }

                    ct.ThrowIfCancellationRequested();
                    var wait = page.WaitForResponseAsync(
                        r => IsChaptersUrl(r.Url) && r.Url.Contains($"page={pageNo}", StringComparison.Ordinal),
                        Wait(PageResponseTimeoutMs, ct));

                    var advanced = await ClickNextPageAsync(page, pageNo, ct);
                    if (!advanced)
                    {
                        logger.LogWarning("MangaFire pager stalled at page {Page}/{Last} for {Series}", pageNo, expected, seriesId);
                    }

                    EnsurePagerAdvanced(advanced, pageNo, expected);

                    await CollectAsync(await wait);
                }

                return ((IReadOnlyList<string>)items.Values.ToList(), languageMatched, true);
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A pager that stops short must not read as a complete list: the sync replaces the mapping's
    /// snapshot with whatever comes back, so a partial walk would drop the links for every chapter on
    /// the pages it never reached.
    /// </summary>
    internal static void EnsurePagerAdvanced(bool advanced, int pageNo, int expected)
    {
        if (!advanced)
        {
            throw new InvalidOperationException($"mangafire: chapter pager stalled at page {pageNo} of {expected}");
        }
    }

    /// <summary>
    /// Outbound links on a title page. The tracker row (MyAnimeList/AniList/MangaUpdates/MangaDex) is
    /// rendered from the detail payload but isn't in it, so unlike every other call here this reads the
    /// DOM rather than a captured API response.
    /// </summary>
    public async Task<IReadOnlyList<string>> ExternalLinksAsync(string seriesId, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await WithPageAsync(async page =>
            {
                var response = await page.GoToAsync($"{BaseUrl}/title/{seriesId}", Navigation(ct));

                if (response?.Status == HttpStatusCode.Forbidden || await ClassifyAsync(page) is not PageVerdict.Unknown)
                {
                    throw new ChallengeException();
                }

                // The row is painted once the SPA's detail call resolves. A title that genuinely links
                // nowhere never paints one, so a timeout here is an ordinary outcome, not a failure:
                // fall through and return whatever links the page does have.
                try
                {
                    await page.WaitForSelectorAsync(
                        ".title-detail__trackers a[href]",
                        new WaitForSelectorOptions { Visible = true, Timeout = TrackerRowTimeoutMs });
                }
                catch (Exception ex) when ((ex is PuppeteerException or TimeoutException) && !ct.IsCancellationRequested)
                {
                    // no tracker row on this title
                }

                return (IReadOnlyList<string>)await page.EvaluateExpressionAsync<string[]>(
                    "[...document.querySelectorAll('a[href]')].map(e => e.href)");
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
                var wait = page.WaitForResponseAsync(r => matches(r.Url), Wait(ResponseTimeoutMs, ct));
                var response = await NavigateAndCaptureAsync(page, wait, navUrl, ct);
                return await BrowserSupport.ReadTextAsync(response, ct);
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
        _idle.Touch();
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

                logger.LogWarning("MangaFire browser could not open a page; relaunching it");
                await ShutdownAsync();
                continue;
            }

            var page = opened;

            try
            {
                return await CancellableBrowserCall.RunAsync(() => BrowserSupport.CloseQuietlyAsync(page), () => action(page), ct);
            }
            catch (NavigationException ex) when (BrowserSupport.ProxyFailure(ex, _route, SourceName) is { } proxyFailure)
            {
                throw proxyFailure;
            }
            catch (ChallengeException) when (attempt == 0)
            {
                logger.LogInformation("MangaFire browser hit a challenge; re-solving clearance and retrying");
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

    private async Task<(IBrowserContext Context, BrowserProfile Profile)> EnsureContextAsync(CancellationToken ct)
    {
        if (_browser is { IsConnected: false })
        {
            logger.LogWarning("The MangaFire browser process is gone; relaunching it");
            await ShutdownAsync();
        }

        // Resolved once per context: the context's proxy, its clearance and every request it makes
        // share one egress for as long as the context lives.
        var route = await fetcher.RouteForAsync(SourceName, ct);
        if (_context != null && _profile != null)
        {
            if (route.Equals(_route))
            {
                return (_context, _profile);
            }

            logger.LogInformation("MangaFire's proxy setting changed; starting a new browser context");
            await ResetContextAsync();
        }

        var session = await fetcher.GetBrowserSessionAsync($"{BaseUrl}/home", route, ct);

        if (_browser == null)
        {
            // The ~100 MB headless shell, not full Chromium: we never render headed, and it keeps the
            // Docker image far smaller.
            var args = new List<string>();
            var resolverRules = await settings.GetAsync(SettingKeys.MangaFireBrowserHostResolverRules, ct);
            if (!string.IsNullOrWhiteSpace(resolverRules))
            {
                args.Add($"--host-resolver-rules={resolverRules}");
            }

            _browser = await launcher.LaunchAsync(args, ct);
        }

        var context = await _browser.CreateBrowserContextAsync(BrowserSupport.ContextOptions(route));
        _route = route;
        await context.SetCookieAsync(BrowserSupport.SessionCookies(session.Cookies, Host));

        _profile = new BrowserProfile(
            session.UserAgent,
            1280,
            900,
            // The headless shell advertises itself in the client hints ("HeadlessChrome") even though
            // the UA header is overridden, and the mismatch between the two is a decisive bot signal:
            // Cloudflare answers it with an outright "Access denied" block (not a solvable challenge)
            // wherever the egress IP's reputation is anything short of pristine. Restate the hints so
            // they agree with the UA FlareSolverr earned the clearance cookie with.
            BrowserSupport.ClientHintsFor(session.UserAgent),
            [
                BrowserSupport.HideWebDriver,
                // The site remembers the last chapter-list language/filter in web storage, and the
                // context is shared across every series we scrape, so a title would otherwise load
                // carrying the previous title's selection, which desynchronises the pager mid-walk.
                // Start every navigation clean.
                "try { localStorage.clear(); sessionStorage.clear(); } catch (e) { }",
            ],
            // only the JSON responses matter: skip images/media/fonts and the analytics and share
            // scripts to cut nav time, bandwidth and renderer work.
            request => BrowserSupport.IsImage(request) ||
                       request.ResourceType is ResourceType.Media or ResourceType.Font ||
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

    /// <summary>
    /// MangaFire language codes to the label its "Lang" dropdown shows (minus the flag emoji).
    /// Only the codes seen in the wild are certain (en, es-la, pt-br); the rest follow the same
    /// English-name convention and cost nothing when wrong: an unmatched code falls back to "All".
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
    private static async Task<IResponse?> SwitchLanguageAsync(IPage page, string wanted, string loaded, CancellationToken ct)
    {
        try
        {
            if (!await page.EvaluateFunctionAsync<bool>(OpenLanguageMenuScript))
            {
                return null;
            }
        }
        catch (PuppeteerException)
        {
            return null;
        }

        await Task.Delay(300, ct);

        var wait = page.WaitForResponseAsync(
            r => IsChaptersUrl(r.Url) &&
                 !string.Equals(QueryParam(r.Url, "language") ?? string.Empty, loaded, StringComparison.OrdinalIgnoreCase),
            Wait(PageResponseTimeoutMs, ct));

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
        await page.EvaluateFunctionAsync<bool>(ClickMenuItemScript, label);

    internal const string ClickMenuItemScript =
        """
        (label) => {
          const norm = s => (s || '').replace(/[^A-Za-z()\s-]/g, '').replace(/\s+/g, ' ').trim().toLowerCase();
          const items = [...document.querySelectorAll('.dropdown__menu button, .dropdown__menu [role=menuitem]')];
          const target = items.find(e => norm(e.textContent) === norm(label));
          if (!target) return false;
          target.dispatchEvent(new MouseEvent('click', { bubbles: true }));
          return true;
        }
        """;

    /// <summary>Opens the "Lang" dropdown: the first <c>button.select</c> whose text contains "lang",
    /// as <c>Locator(HasText)</c> matched it.</summary>
    internal const string OpenLanguageMenuScript =
        """
        () => {
          const menu = [...document.querySelectorAll('button.select')]
            .find(b => (b.textContent || '').toLowerCase().includes('lang'));
          if (!menu) return false;
          menu.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, composed: true }));
          return true;
        }
        """;

    /// <summary>
    /// Clicks the first usable pager button, the way the Playwright locators this replaced did. In
    /// order: the "Next page" arrow by class, then any button whose accessible name contains "next
    /// page" (case-insensitive), then the numbered pager button reading exactly <c>pageNo</c>.
    /// <para>
    /// The two CSS candidates take their first match and give up on it when it is hidden or disabled,
    /// as <c>Locator.First</c> did. The role candidate skips matches hidden from the accessibility tree
    /// first, as <c>GetByRole</c> did, and names each button the way the accessible name is computed:
    /// <c>aria-labelledby</c>, then <c>aria-label</c>, then the text, then <c>title</c>. Disabled means
    /// what Playwright's <c>IsEnabled</c> meant: the control itself, a disabled fieldset outside its
    /// legend, or <c>aria-disabled</c> on it or any ancestor.
    /// </para>
    /// </summary>
    internal const string ClickPagerScript =
        """
        (pageNo) => {
          const text = el => (el.innerText ?? el.textContent ?? '').replace(/\s+/g, ' ').trim();
          const visible = el => {
            const box = el.getBoundingClientRect();
            return box.width > 0 && box.height > 0 && getComputedStyle(el).visibility === 'visible';
          };
          const disabled = el => {
            if (el.disabled) return true;
            const fieldset = el.closest('fieldset[disabled]');
            if (fieldset) {
              const legend = [...fieldset.children].find(c => c.tagName === 'LEGEND');
              if (!legend || !legend.contains(el)) return true;
            }
            for (let e = el; e; e = e.parentElement) {
              if (e.getAttribute('aria-disabled') === 'true') return true;
            }
            return false;
          };
          const usable = el => !!el && el.isConnected && visible(el) && !disabled(el);
          const hiddenFromAria = el =>
            !el.checkVisibility({ visibilityProperty: true }) || !!el.closest('[aria-hidden="true"], [inert]');
          const accessibleName = el => {
            const ids = (el.getAttribute('aria-labelledby') || '').split(/\s+/).filter(Boolean);
            const labelled = ids.map(id => document.getElementById(id)).filter(Boolean).map(text).join(' ').trim();
            if (labelled) return labelled;
            const label = (el.getAttribute('aria-label') || '').trim();
            if (label) return label;
            const content = text(el);
            if (content) return content;
            return (el.getAttribute('title') || '').trim();
          };
          const roleButtons = [...document.querySelectorAll(
            'button:not([role]), [role=button], input[type=button]:not([role]), input[type=submit]:not([role]), input[type=reset]:not([role])')];
          const candidates = [
            document.querySelector("[class*=pager] button[aria-label='Next page']"),
            roleButtons.find(b => !hiddenFromAria(b) && accessibleName(b).replace(/\s+/g, ' ').toLowerCase().includes('next page')),
            [...document.querySelectorAll('[class*=pager] button')].find(b => text(b) === String(pageNo)),
          ];
          for (const target of candidates) {
            if (!usable(target)) continue;
            target.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, composed: true }));
            return true;
          }
          return false;
        }
        """;

    /// <summary>
    /// Advances the chapter pager to <paramref name="pageNo"/>. Uses the "Next page" arrow while it's
    /// present, falling back to the numbered button once the arrow drops out of the final window.
    /// Fires the click as a dispatched event because the pager re-renders (shifting its number window)
    /// and a normal actionable click races the detach.
    /// </summary>
    private static async Task<bool> ClickNextPageAsync(IPage page, int pageNo, CancellationToken ct)
    {
        // The pager is briefly absent mid-re-render, so a single sweep of the candidates can find
        // nothing clickable on a page that is perfectly reachable a moment later; sweep again.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(400, ct);
            }

            // pager may be mid-render (element unstable/absent); the candidate sweep below reports the real outcome
            await ScrollIntoViewAsync(page, "[class*=pager]", 3000, ct);

            try
            {
                if (await page.EvaluateFunctionAsync<bool>(ClickPagerScript, pageNo))
                {
                    return true;
                }
            }
            catch (PuppeteerException)
            {
                // the page re-rendered under the script; sweep again
            }
        }

        return false;
    }

    /// <summary>Waits for <paramref name="selector"/> to be visible and scrolls it into view; quietly
    /// does nothing when it never shows up.</summary>
    private static async Task ScrollIntoViewAsync(IPage page, string selector, int timeoutMs, CancellationToken ct)
    {
        try
        {
            await using var element = await page.WaitForSelectorAsync(
                selector, new WaitForSelectorOptions { Visible = true, Timeout = timeoutMs });
            if (element is not null)
            {
                await element.EvaluateFunctionAsync("e => e.scrollIntoView({ block: 'center', inline: 'center' })");
            }
        }
        catch (Exception ex) when ((ex is PuppeteerException or TimeoutException) && !ct.IsCancellationRequested)
        {
        }
    }

    private static WaitForOptions Wait(int timeoutMs, CancellationToken ct) =>
        new() { Timeout = timeoutMs, CancellationToken = ct };

    private static NavigationOptions Navigation(CancellationToken ct) => new()
    {
        WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
        Timeout = NavTimeoutMs,
        CancellationToken = ct,
    };

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
        await ShutdownAsync();
        _gate.Dispose();
    }

    /// <summary>Navigate, then await the pre-armed response wait, turning an opaque timeout into a
    /// diagnosable cause (Cloudflare challenge vs. a page that simply never issued the request).</summary>
    private async Task<IResponse> NavigateAndCaptureAsync(IPage page, Task<IResponse> waitTask, string navUrl, CancellationToken ct)
    {
        // What the page *did* ask for is the only thing that separates the remaining failure modes:
        // the site renaming the endpoint (some other /api response arrives) from it never issuing the
        // call at all (results rendered server-side, or a boot the SPA never finished). Without the
        // list the timeout says the same thing for both and there is nothing to act on.
        var seen = new List<string>();
        void Record(object? _, ResponseCreatedEventArgs e)
        {
            var r = e.Response;
            if (!r.Url.Contains("/api/", StringComparison.Ordinal))
            {
                return;
            }

            lock (seen)
            {
                if (seen.Count < 15 && !seen.Contains(r.Url))
                {
                    seen.Add(r.Url);
                }
            }
        }

        page.Response += Record;
        try
        {
            return await NavigateAndCaptureCoreAsync(page, waitTask, navUrl, seen, ct);
        }
        finally
        {
            page.Response -= Record;
        }
    }

    private async Task<IResponse> NavigateAndCaptureCoreAsync(
        IPage page, Task<IResponse> waitTask, string navUrl, List<string> seen, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        await page.GoToAsync(navUrl, Navigation(ct));
        var navMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

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
                        "MangaFire: the headless browser did not clear Cloudflare: the FlareSolverr clearance " +
                        "cookie was rejected. This usually means Maki's network egress IP differs from " +
                        "FlareSolverr's (the cookie is IP-bound); run both behind the same egress or proxy. A proxy " +
                        "whose outgoing IP rotates between requests breaks it the same way.");
                case PageVerdict.Blocked:
                    throw new ChallengeException(
                        "MangaFire: Cloudflare served an 'Access denied' block page. This is a firewall/bot-score " +
                        "rejection, not a solvable challenge, so re-solving won't help on its own. It's driven by the " +
                        "egress IP's reputation (VPN/VPS ranges score badly) plus the headless browser's fingerprint; " +
                        "route Maki's traffic through a residential-grade egress if it persists.");
            }

            var title = await SafeTitleAsync(page);
            string apiSeen;
            lock (seen)
            {
                apiSeen = seen.Count == 0 ? "none" : string.Join(", ", seen);
            }

            throw new InvalidOperationException(
                $"MangaFire: '{navUrl}' loaded but the expected API request never fired " +
                $"(page title '{title}', url {page.Url}, navigation {navMs} ms, /api responses seen: {apiSeen}).");
        }

        if (response.Status == HttpStatusCode.Forbidden)
        {
            throw new ChallengeException();
        }

        return response;
    }

    private static readonly string[] BlockedTitleContains = ["Access denied", "Attention Required"];
    private static readonly string[] BlockedContentContains =
        ["used Cloudflare to restrict access", "Error code 1020", "error code: 1020"];

    private static Task<PageVerdict> ClassifyAsync(IPage page) =>
        CloudflareChallengeDetection.ClassifyAsync(page, BlockedTitleContains, BlockedContentContains);

    private static async Task<string> SafeTitleAsync(IPage page)
    {
        try
        {
            return await page.GetTitleAsync();
        }
        catch (PuppeteerException)
        {
            return "(unavailable)";
        }
    }
}
