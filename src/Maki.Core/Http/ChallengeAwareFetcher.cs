using System.Collections.Concurrent;
using System.Net;
using Maki.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Maki.Core.Http;

/// <summary>
/// HTML fetcher for anti-bot-protected sites. Tries a direct request with any
/// cached clearance cookies first; when a challenge is detected it solves it via
/// FlareSolverr, caches the cookies + user agent per host, and retries.
/// </summary>
public class ChallengeAwareFetcher(
    IHttpClientFactory httpClientFactory,
    FlareSolverrClient flareSolverr,
    IAppSettings settings,
    ILogger<ChallengeAwareFetcher> logger) : IHtmlFetcher
{
    public const string HttpClientName = "challenge-fetcher";

    private record HostSession(string CookieHeader, string UserAgent);

    private readonly ConcurrentDictionary<string, HostSession> _sessions = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _solveGates = new();

    public Task<string> GetHtmlAsync(string url, CancellationToken ct = default) =>
        FetchAsync(new HtmlFetchRequest(url), ct);

    public async Task<string> FetchAsync(HtmlFetchRequest request, CancellationToken ct = default)
    {
        var url = request.Url;
        var host = new Uri(url).Host;

        _sessions.TryGetValue(host, out var session);
        if (await TryDirectAsync(request, session, ct) is { } direct)
        {
            return direct;
        }

        if (session != null)
        {
            _sessions.TryRemove(KeyValuePair.Create(host, session));
        }

        var gate = _solveGates.GetOrAdd(host, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            // Another caller may have solved this host while this one queued; reuse its clearance
            // instead of starting a second solve.
            if (_sessions.TryGetValue(host, out var solved) && !ReferenceEquals(solved, session))
            {
                if (await TryDirectAsync(request, solved, ct) is { } retried)
                {
                    return retried;
                }

                _sessions.TryRemove(KeyValuePair.Create(host, solved));
            }

            var flareUrl = await settings.GetAsync(SettingKeys.FlareSolverrUrl, ct);
            if (string.IsNullOrWhiteSpace(flareUrl))
            {
                throw new InvalidOperationException(
                    $"{host} requires solving an anti-bot challenge; configure a FlareSolverr URL in Settings");
            }

            logger.LogInformation("Solving challenge for {Host} via FlareSolverr", host);
            var solution = await flareSolverr.SolveAsync(flareUrl, url, request.FormBody, request.Cookies, ct);
            StoreSession(host, solution);

            // FlareSolverr says "ok" for whatever the origin served, so a ban or 404 page would
            // otherwise reach the parser as content.
            if (solution.Status >= 400)
            {
                throw new HttpRequestException(
                    $"{host} answered HTTP {solution.Status} through FlareSolverr", null, (HttpStatusCode)solution.Status);
            }

            return solution.Html;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Solved clearance cookies + user agent for a host, for handing to a real browser.</summary>
    public record BrowserSession(IReadOnlyDictionary<string, string> Cookies, string UserAgent);

    /// <summary>
    /// Ensures an anti-bot challenge is solved for the host and returns the clearance cookies + UA,
    /// so a headless browser (MangaFire's vrf signer needs one) can start already past Cloudflare
    /// instead of solving it again. Reuses a cached session when present.
    /// </summary>
    public async Task<BrowserSession> GetBrowserSessionAsync(string url, CancellationToken ct = default)
    {
        var host = new Uri(url).Host;
        if (_sessions.TryGetValue(host, out var cached))
        {
            return new BrowserSession(ParseCookieHeader(cached.CookieHeader), cached.UserAgent);
        }

        var gate = _solveGates.GetOrAdd(host, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_sessions.TryGetValue(host, out cached))
            {
                return new BrowserSession(ParseCookieHeader(cached.CookieHeader), cached.UserAgent);
            }

            var flareUrl = await settings.GetAsync(SettingKeys.FlareSolverrUrl, ct);
            if (string.IsNullOrWhiteSpace(flareUrl))
            {
                throw new InvalidOperationException(
                    $"{host} requires a browser session; configure a FlareSolverr URL in Settings");
            }

            logger.LogInformation("Solving challenge for {Host} via FlareSolverr (browser session)", host);
            var solution = await flareSolverr.GetAsync(flareUrl, url, ct);
            StoreSession(host, solution);
            return new BrowserSession(solution.Cookies, solution.UserAgent);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Drops a cached session so the next call re-solves (e.g. after a browser 403).</summary>
    public void InvalidateSession(string host) => _sessions.TryRemove(host, out _);

    private void StoreSession(string host, FlareSolverrClient.FlareSolution solution)
    {
        if (solution.Cookies.Count > 0 && !string.IsNullOrEmpty(solution.UserAgent))
        {
            var cookieHeader = string.Join("; ", solution.Cookies.Select(c => $"{c.Key}={c.Value}"));
            _sessions[host] = new HostSession(cookieHeader, solution.UserAgent);
        }
    }

    private static IReadOnlyDictionary<string, string> ParseCookieHeader(string header)
    {
        var cookies = new Dictionary<string, string>();
        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0)
            {
                cookies[part[..eq]] = part[(eq + 1)..];
            }
        }

        return cookies;
    }

    /// <summary>Headers page downloads must send to reuse the solved session (Cookie + UA).</summary>
    public IReadOnlyDictionary<string, string> SessionHeadersFor(string host, string referer)
    {
        var headers = new Dictionary<string, string> { ["Referer"] = referer };
        if (_sessions.TryGetValue(host, out var session))
        {
            headers["Cookie"] = session.CookieHeader;
            headers["User-Agent"] = session.UserAgent;
        }

        return headers;
    }

    /// <summary>
    /// The body, or null when the response is a challenge worth handing to FlareSolverr. Any other
    /// failure (404, 500, DNS, TLS) is thrown: FlareSolverr cannot fix it, and treating it as a
    /// challenge would throw away a good clearance for every other caller on the host.
    /// </summary>
    private async Task<string?> TryDirectAsync(HtmlFetchRequest fetch, HostSession? session, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(
            fetch.FormBody is null ? HttpMethod.Get : HttpMethod.Post, fetch.Url);
        if (fetch.FormBody is not null)
        {
            request.Content = new StringContent(fetch.FormBody, System.Text.Encoding.UTF8, "application/x-www-form-urlencoded");
        }

        var cookieHeader = MergeCookies(session?.CookieHeader, fetch.Cookies);
        if (cookieHeader is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        if (session != null)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", session.UserAgent);
        }

        using var response = await client.SendAsync(request, ct);
        if ((int)response.StatusCode is 403 or 503 || IsMitigated(response))
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(ct);
        return LooksLikeChallenge(html) ? null : html;
    }

    private static bool IsMitigated(HttpResponseMessage response) =>
        response.Headers.TryGetValues("cf-mitigated", out var values)
        && values.Any(v => v.Contains("challenge", StringComparison.OrdinalIgnoreCase));

    /// <summary>Solved clearance cookies first, the request's own on top; the request wins a name clash.</summary>
    private static string? MergeCookies(string? sessionHeader, IReadOnlyDictionary<string, string>? extra)
    {
        if (extra is not { Count: > 0 })
        {
            return sessionHeader;
        }

        var merged = sessionHeader is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string>(ParseCookieHeader(sessionHeader));
        foreach (var (name, value) in extra)
        {
            merged[name] = value;
        }

        return string.Join("; ", merged.Select(c => $"{c.Key}={c.Value}"));
    }

    // No "challenge-platform": Cloudflare's JS detections inject /cdn-cgi/challenge-platform/ into
    // healthy pages too.
    private static bool LooksLikeChallenge(string html)
    {
        if (html.Length > 20_000)
        {
            return false; // real pages are big; challenge shells are tiny
        }

        return html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
            || html.Contains("cf-challenge", StringComparison.OrdinalIgnoreCase)
            || html.Contains("cf-chl", StringComparison.OrdinalIgnoreCase)
            || html.Contains("__cf_chl_", StringComparison.OrdinalIgnoreCase)
            || html.Contains("document.write(\"<scr\"+\"ipt>", StringComparison.OrdinalIgnoreCase);
    }
}
