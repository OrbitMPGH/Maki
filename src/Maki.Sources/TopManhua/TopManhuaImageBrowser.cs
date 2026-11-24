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
    ILogger<TopManhuaImageBrowser> logger,
    string? stagingRoot = null) : IAsyncDisposable, IIdleBrowser
{
    private const string BaseUrl = "https://www.topmanhua.fan";
    private const string Host = "www.topmanhua.fan";
    private const string SourceName = "topmanhua";
    private const int NavTimeoutMs = 30_000;
    private const int BatchTimeoutMs = 15_000;
    private const int CaptureTimeoutMs = 45_000;
    private const int MaxCaptureTimeoutMs = 180_000;
    private const int ImageBatchSize = 4;
    private const int MaxBodiesInFlight = 2;
    private static readonly TimeSpan StaleStagingAge = TimeSpan.FromHours(1);
    private static readonly ConcurrentDictionary<string, byte> ActiveStagingDirs = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _stagingRoot = string.IsNullOrWhiteSpace(stagingRoot)
        ? Path.Combine(Path.GetTempPath(), "maki-topmanhua")
        : stagingRoot;

    /// <summary>
    /// Captured chapter page images staged on disk instead of held in RAM across the chapter.
    /// Dispose deletes the staging directory.
    /// </summary>
    public sealed class StagedImages : IDisposable
    {
        private readonly string _directory;
        private readonly string _fullPath;
        private readonly IReadOnlyDictionary<string, string> _paths;
        private int _disposed;

        public StagedImages(string directory, IReadOnlyDictionary<string, string> paths)
        {
            _directory = directory;
            _fullPath = Path.GetFullPath(directory);
            _paths = paths;
            ActiveStagingDirs[_fullPath] = 0;
        }

        public IReadOnlyDictionary<string, string> Paths => _paths;

        public bool TryGetPath(string url, out string path) => _paths.TryGetValue(url, out path!);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            ActiveStagingDirs.TryRemove(_fullPath, out _);
            TryDeleteDirectory(_directory);
        }
    }

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
    /// Navigates to <paramref name="chapterUrl"/>, loads lazy-loaded reading-content images in small
    /// batches (removing completed &lt;img&gt; nodes between batches so Chromium drops decoded bitmaps),
    /// and stages captured bytes to temp files keyed by URL. A URL missing from the result simply never
    /// loaded in time; the caller falls back to a plain fetch for those rather than failing the whole chapter.
    /// </summary>
    public async Task<StagedImages> FetchImagesAsync(
        string chapterUrl, IReadOnlyList<string> imageUrls, CancellationToken ct)
    {
        SweepStaleStagingDirs(_stagingRoot);
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

    private async Task<StagedImages> CaptureAsync(
        IPage page, string chapterUrl, IReadOnlyList<string> imageUrls, CancellationToken ct)
    {
        var wanted = new HashSet<string>(imageUrls, StringComparer.Ordinal);
        var stagingDir = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N"));
        var fullStagingDir = Path.GetFullPath(stagingDir);
        Directory.CreateDirectory(stagingDir);
        ActiveStagingDirs[fullStagingDir] = 0;
        var captured = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var claimed = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var failed = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        var activeCaptures = new ConcurrentDictionary<Task, byte>(ReferenceEqualityComparer.Instance);
        var fileIndex = 0;
        var admitting = 1;
        StagedImages? staged = null;
        try
        {
        using var captureCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Each body travels from Chromium as base64 JSON, so a 1 MB page is several MB of transient
        // garbage; bounding in-flight reads and streaming each one to disk keeps managed heap flat.
        var inFlight = new SemaphoreSlim(MaxBodiesInFlight);

        async Task CaptureBodyAsync(IResponse response)
        {
            if (Volatile.Read(ref admitting) == 0 || !wanted.Contains(response.Url) || captured.ContainsKey(response.Url))
            {
                return;
            }

            if (!response.Ok)
            {
                if (IsFailureStatus((int)response.Status) && !captured.ContainsKey(response.Url))
                {
                    failed[response.Url] = 0;
                }

                return;
            }

            if (!claimed.TryAdd(response.Url, 0))
            {
                return;
            }

            var stored = false;
            try
            {
                await inFlight.WaitAsync(captureCts.Token);
                try
                {
                    if (Volatile.Read(ref admitting) == 0 || captured.ContainsKey(response.Url))
                    {
                        return;
                    }

                    var bytes = await BrowserSupport.ReadBytesAsync(response, captureCts.Token);
                    if (Volatile.Read(ref admitting) == 0)
                    {
                        return;
                    }

                    var path = Path.Combine(stagingDir, $"{Interlocked.Increment(ref fileIndex):0000}.bin");
                    await File.WriteAllBytesAsync(path, bytes, captureCts.Token);
                    captured[response.Url] = path;
                    failed.TryRemove(response.Url, out _);
                    stored = true;
                }
                finally
                {
                    inFlight.Release();
                }
            }
            catch (Exception)
            {
                // best-effort capture: the body may be gone (page closed or navigated away); skip it
                if (!captured.ContainsKey(response.Url))
                {
                    failed[response.Url] = 0;
                }
            }
            finally
            {
                if (!stored)
                {
                    claimed.TryRemove(response.Url, out _);
                }
            }
        }

        void OnResponse(object? sender, ResponseCreatedEventArgs e)
        {
            if (Volatile.Read(ref admitting) == 0)
            {
                return;
            }

            var task = CaptureBodyAsync(e.Response);
            activeCaptures[task] = 0;
            _ = task.ContinueWith(
                static (t, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(t, out _),
                activeCaptures, TaskScheduler.Default);
        }

        void OnRequestFailed(object? sender, RequestEventArgs e)
        {
            if (Volatile.Read(ref admitting) == 0)
            {
                return;
            }

            if (wanted.Contains(e.Request.Url) && !captured.ContainsKey(e.Request.Url))
            {
                failed[e.Request.Url] = 0;
            }
        }

        page.Response += OnResponse;
        page.RequestFailed += OnRequestFailed;
        var succeeded = false;
        try
        {
            await NavigateAndCaptureAsync(page, chapterUrl, wanted, captured, failed, ct);
            succeeded = true;
        }
        finally
        {
            Volatile.Write(ref admitting, 0);
            page.Response -= OnResponse;
            page.RequestFailed -= OnRequestFailed;
            if (!succeeded)
            {
                captureCts.Cancel();
            }

            var pending = activeCaptures.Keys.ToArray();
            if (pending.Length > 0)
            {
                try
                {
                    await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception)
                {
                }
            }

            if (!succeeded)
            {
                ActiveStagingDirs.TryRemove(fullStagingDir, out _);
                TryDeleteDirectory(stagingDir);
            }
        }

        if (captured.Count < wanted.Count)
        {
            logger.LogWarning(
                "TopManhua browser captured {Captured}/{Wanted} images for {Chapter}; missing ones will fall back to a plain fetch",
                captured.Count, wanted.Count, chapterUrl);
        }

        staged = new StagedImages(stagingDir, captured.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));
        return staged;
        }
        finally
        {
            if (staged is null)
            {
                ActiveStagingDirs.TryRemove(fullStagingDir, out _);
                TryDeleteDirectory(stagingDir);
            }
        }
    }

    internal static bool IsFailureStatus(int statusCode) => statusCode is < 200 or >= 400;

    internal sealed record ActivationBatch(string[] Urls, int Remaining);

    private static async Task NavigateAndCaptureAsync(
        IPage page, string chapterUrl, HashSet<string> wanted,
        ConcurrentDictionary<string, string> captured, ConcurrentDictionary<string, byte> failed,
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

        var overallTimeout = TimeSpan.FromMilliseconds(
            Math.Clamp(Math.Max(CaptureTimeoutMs, wanted.Count * 2_000), CaptureTimeoutMs, MaxCaptureTimeoutMs));

        await DriveSlidingWindowAsync(
            wanted,
            captured,
            failed,
            async needed =>
            {
                var result = await page.EvaluateFunctionAsync<ActivationBatch>(
                    """
                    (needed) => {
                        const imgs = document.querySelectorAll('.reading-content img[data-src]');
                        const urls = [];
                        for (const img of imgs) {
                            if (urls.length >= needed) break;
                            const url = (img.dataset.src || '').trim();
                            img.removeAttribute('data-src');
                            if (!url) continue;
                            img.dataset.makiUrl = url;
                            img.src = url;
                            urls.push(url);
                        }
                        const remaining = document.querySelectorAll('.reading-content img[data-src]').length;
                        return { urls, remaining };
                    }
                    """,
                    needed);
                return result ?? new ActivationBatch([], 0);
            },
            urls => page.EvaluateFunctionAsync(
                """
                (urls) => {
                    const set = new Set(urls);
                    for (const img of document.querySelectorAll('.reading-content img[data-maki-url]')) {
                        if (set.has(img.dataset.makiUrl)) {
                            img.removeAttribute('src');
                            img.removeAttribute('srcset');
                            img.remove();
                        }
                    }
                }
                """,
                urls),
            () => page.EvaluateExpressionAsync(
                """
                (() => {
                    for (const img of document.querySelectorAll('.reading-content img[data-maki-url]')) {
                        img.removeAttribute('src');
                        img.removeAttribute('srcset');
                        img.remove();
                    }
                })()
                """),
            TimeSpan.FromMilliseconds(BatchTimeoutMs),
            overallTimeout,
            TimeSpan.FromMilliseconds(50),
            TimeProvider.System,
            ct);
    }

    internal static async Task DriveSlidingWindowAsync(
        HashSet<string> wanted,
        ConcurrentDictionary<string, string> captured,
        ConcurrentDictionary<string, byte> failed,
        Func<int, Task<ActivationBatch>> activateNext,
        Func<IReadOnlyList<string>, Task> removeImages,
        Func<Task> removeAllImages,
        TimeSpan imageTimeout,
        TimeSpan overallTimeout,
        TimeSpan pollInterval,
        TimeProvider time,
        CancellationToken ct)
    {
        var overallDeadline = time.GetUtcNow() + overallTimeout;
        var inFlight = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var domExhausted = false;

        bool AllSettled() => wanted.All(u => captured.ContainsKey(u) || failed.ContainsKey(u));

        // Keep up to ImageBatchSize native <img> fetches in flight as a sliding window, removing
        // each <img> node as soon as it settles so Chromium drops decoded bitmaps without one slow
        // image stalling the rest of its batch.
        while (!AllSettled() && time.GetUtcNow() < overallDeadline)
        {
            ct.ThrowIfCancellationRequested();
            var now = time.GetUtcNow();

            List<string>? settled = null;
            foreach (var (url, deadline) in inFlight)
            {
                if (captured.ContainsKey(url) || failed.ContainsKey(url) || now >= deadline)
                {
                    (settled ??= []).Add(url);
                }
            }

            if (settled is not null)
            {
                foreach (var url in settled)
                {
                    inFlight.Remove(url);
                }

                await removeImages(settled);
            }

            if (!domExhausted && inFlight.Count < ImageBatchSize)
            {
                var needed = ImageBatchSize - inFlight.Count;
                var batch = await activateNext(needed);
                if (batch.Remaining <= 0)
                {
                    domExhausted = true;
                }

                List<string>? discard = null;
                var imageDeadline = now + imageTimeout;
                if (imageDeadline > overallDeadline)
                {
                    imageDeadline = overallDeadline;
                }

                foreach (var url in batch.Urls)
                {
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        continue;
                    }

                    if (!wanted.Contains(url) || captured.ContainsKey(url) || failed.ContainsKey(url))
                    {
                        (discard ??= []).Add(url);
                        continue;
                    }

                    inFlight.TryAdd(url, imageDeadline);
                }

                if (discard is not null)
                {
                    await removeImages(discard);
                }
            }

            if (domExhausted && inFlight.Count == 0)
            {
                break;
            }

            await Task.Delay(pollInterval, time, ct);
        }

        await removeAllImages();

        // Wait briefly for any non-lazy images that were already in flight before the loop ended.
        var tailDeadline = time.GetUtcNow() + imageTimeout;
        if (tailDeadline > overallDeadline)
        {
            tailDeadline = overallDeadline;
        }

        while (!AllSettled() && time.GetUtcNow() < tailDeadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(pollInterval, time, ct);
        }
    }

    internal static void SweepStaleStagingDirs(string root, DateTime? nowUtc = null)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var cutoff = (nowUtc ?? DateTime.UtcNow) - StaleStagingAge;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (ActiveStagingDirs.ContainsKey(Path.GetFullPath(dir)))
                {
                    continue;
                }

                if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                {
                    TryDeleteDirectory(dir);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
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
