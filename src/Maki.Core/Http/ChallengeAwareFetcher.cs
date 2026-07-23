using System.Collections.Concurrent;
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
    ILogger<ChallengeAwareFetcher> logger)
{
    public const string HttpClientName = "challenge-fetcher";

    private record HostSession(string CookieHeader, string UserAgent);

    private readonly ConcurrentDictionary<string, HostSession> _sessions = new();

    public async Task<string> GetHtmlAsync(string url, CancellationToken ct = default)
    {
        var host = new Uri(url).Host;

        if (_sessions.TryGetValue(host, out var session))
        {
            var direct = await TryDirectAsync(url, session, ct);
            if (direct != null)
            {
                return direct;
            }

            _sessions.TryRemove(host, out _);
        }
        else
        {
            var direct = await TryDirectAsync(url, null, ct);
            if (direct != null)
            {
                return direct;
            }
        }

        var flareUrl = await settings.GetAsync(SettingKeys.FlareSolverrUrl, ct);
        if (string.IsNullOrWhiteSpace(flareUrl))
        {
            throw new InvalidOperationException(
                $"{host} requires solving an anti-bot challenge; configure a FlareSolverr URL in Settings");
        }

        logger.LogInformation("Solving challenge for {Host} via FlareSolverr", host);
        var solution = await flareSolverr.GetAsync(flareUrl, url, ct);

        if (solution.Cookies.Count > 0 && !string.IsNullOrEmpty(solution.UserAgent))
        {
            var cookieHeader = string.Join("; ", solution.Cookies.Select(c => $"{c.Key}={c.Value}"));
            _sessions[host] = new HostSession(cookieHeader, solution.UserAgent);
        }

        return solution.Html;
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

        var flareUrl = await settings.GetAsync(SettingKeys.FlareSolverrUrl, ct);
        if (string.IsNullOrWhiteSpace(flareUrl))
        {
            throw new InvalidOperationException(
                $"{host} requires a browser session; configure a FlareSolverr URL in Settings");
        }

        logger.LogInformation("Solving challenge for {Host} via FlareSolverr (browser session)", host);
        var solution = await flareSolverr.GetAsync(flareUrl, url, ct);
        if (solution.Cookies.Count > 0 && !string.IsNullOrEmpty(solution.UserAgent))
        {
            var cookieHeader = string.Join("; ", solution.Cookies.Select(c => $"{c.Key}={c.Value}"));
            _sessions[host] = new HostSession(cookieHeader, solution.UserAgent);
        }

        return new BrowserSession(solution.Cookies, solution.UserAgent);
    }

    /// <summary>Drops a cached session so the next call re-solves (e.g. after a browser 403).</summary>
    public void InvalidateSession(string host) => _sessions.TryRemove(host, out _);

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

    private async Task<string?> TryDirectAsync(string url, HostSession? session, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (session != null)
            {
                request.Headers.TryAddWithoutValidation("Cookie", session.CookieHeader);
                request.Headers.TryAddWithoutValidation("User-Agent", session.UserAgent);
            }

            using var response = await client.SendAsync(request, ct);
            if ((int)response.StatusCode is 403 or 503)
            {
                return null; // classic Cloudflare challenge status
            }

            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(ct);
            return LooksLikeChallenge(html) ? null : html;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static bool LooksLikeChallenge(string html)
    {
        if (html.Length > 20_000)
        {
            return false; // real pages are big; challenge shells are tiny
        }

        return html.Contains("cf-challenge", StringComparison.OrdinalIgnoreCase)
            || html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
            || html.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase)
            || html.Contains("document.write(\"<scr\"+\"ipt>", StringComparison.OrdinalIgnoreCase);
    }
}
