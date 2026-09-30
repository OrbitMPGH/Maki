using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Maki.Core.Http;

/// <summary>Thin client for a FlareSolverr instance (POST /v1, cmd=request.get or request.post).</summary>
public class FlareSolverrClient(IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = "flaresolverr";

    // Each solve is a browser tab in FlareSolverr for up to maxTimeout; a small NAS box runs out of
    // memory well before the download pool runs out of callers.
    private const int MaxConcurrentSolves = 2;

    private readonly SemaphoreSlim _solves = new(MaxConcurrentSolves, MaxConcurrentSolves);

    public record FlareSolution(
        int Status,
        string Html,
        string UserAgent,
        IReadOnlyDictionary<string, string> Cookies);

    public Task<FlareSolution> GetAsync(string flareSolverrUrl, string targetUrl, CancellationToken ct = default) =>
        SolveAsync(flareSolverrUrl, targetUrl, postData: null, cookies: null, ct);

    /// <summary>
    /// Solves <paramref name="targetUrl"/>. A non-null <paramref name="postData"/> switches the command
    /// to <c>request.post</c> (form-urlencoded string, the only body FlareSolverr takes), and
    /// <paramref name="cookies"/> are set in the browser before navigation. FlareSolverr v3 dropped
    /// custom request headers, so cookies are the only per-request state it still accepts.
    /// </summary>
    public async Task<FlareSolution> SolveAsync(
        string flareSolverrUrl,
        string targetUrl,
        string? postData,
        IReadOnlyDictionary<string, string>? cookies,
        CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var endpoint = flareSolverrUrl.TrimEnd('/') + "/v1";

        var payload = new Dictionary<string, object>
        {
            ["cmd"] = postData is null ? "request.get" : "request.post",
            ["url"] = targetUrl,
            ["maxTimeout"] = 60000
        };
        if (postData is not null)
        {
            payload["postData"] = postData;
        }

        if (cookies is { Count: > 0 })
        {
            payload["cookies"] = cookies.Select(c => new { name = c.Key, value = c.Value }).ToArray();
        }

        FlareResponse body;
        await _solves.WaitAsync(ct);
        try
        {
            using var response = await client.PostAsJsonAsync(endpoint, payload, ct);
            response.EnsureSuccessStatusCode();

            body = await response.Content.ReadFromJsonAsync<FlareResponse>(ct)
                ?? throw new InvalidOperationException("FlareSolverr returned an empty response");
        }
        finally
        {
            _solves.Release();
        }

        if (body.Status != "ok" || body.Solution is null)
        {
            throw new InvalidOperationException($"FlareSolverr failed: {body.Message ?? body.Status}");
        }

        return new FlareSolution(
            body.Solution.Status,
            body.Solution.Response ?? string.Empty,
            body.Solution.UserAgent ?? string.Empty,
            CookieMap(body.Solution.Cookies, new Uri(targetUrl).Host));
    }

    /// <summary>
    /// The browser jar can hold one name twice under different domains or paths. A cookie scoped to
    /// the target host beats one that is not; otherwise the last one wins.
    /// </summary>
    private static Dictionary<string, string> CookieMap(List<FlareCookie> cookies, string host)
    {
        var map = new Dictionary<string, string>();
        var hostScoped = new HashSet<string>();
        foreach (var cookie in cookies)
        {
            var matches = DomainMatches(cookie.Domain, host);
            if (matches || !hostScoped.Contains(cookie.Name))
            {
                map[cookie.Name] = cookie.Value;
            }

            if (matches)
            {
                hostScoped.Add(cookie.Name);
            }
        }

        return map;
    }

    private static bool DomainMatches(string? domain, string host)
    {
        if (string.IsNullOrEmpty(domain))
        {
            return true;
        }

        var bare = domain.TrimStart('.');
        return host.Equals(bare, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + bare, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Checks the instance is alive (GET / returns a ready message).</summary>
    public async Task<bool> PingAsync(string flareSolverrUrl, CancellationToken ct = default)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            var response = await client.GetAsync(flareSolverrUrl.TrimEnd('/') + "/", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private class FlareResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("solution")]
        public FlareSolutionDto? Solution { get; set; }
    }

    private class FlareSolutionDto
    {
        [JsonPropertyName("status")]
        public int Status { get; set; }

        [JsonPropertyName("response")]
        public string? Response { get; set; }

        [JsonPropertyName("userAgent")]
        public string? UserAgent { get; set; }

        [JsonPropertyName("cookies")]
        public List<FlareCookie> Cookies { get; set; } = [];
    }

    private class FlareCookie
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("value")]
        public string Value { get; set; } = string.Empty;

        [JsonPropertyName("domain")]
        public string? Domain { get; set; }
    }
}
