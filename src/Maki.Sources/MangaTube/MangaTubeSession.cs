using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Maki.Sources.MangaTube;

/// <summary>The parsed body of a Manga-Tube response, kept as a string so both the challenge
/// detector and the JSON caller can read it without consuming the content stream twice.</summary>
internal readonly record struct MangaTubeResponse(HttpStatusCode Status, string? ContentType, string Body)
{
    public JsonElement AsJson()
    {
        using var doc = JsonDocument.Parse(Body);
        return doc.RootElement.Clone();
    }
}

internal readonly record struct MangaTubeChallenge(string Arg1, string Arg2, string Arg3, string Token);

/// <summary>
/// Every request to manga-tube.me, API included, first answers with a home-grown arithmetic
/// challenge (200 text/html, "Verifying the connection. Please wait...") unless it carries a
/// valid <c>__mtbpass</c> cookie. This holds that cookie behind a lock and re-solves once when a
/// request comes back challenged, rather than relying on the DI-owned HttpClient's own state:
/// IHttpClientFactory rotates primary handlers every couple of minutes, which would silently drop
/// a CookieContainer-held pass and force a re-solve on an unpredictable cadence.
/// </summary>
internal sealed partial class MangaTubeSession(IHttpClientFactory httpClientFactory, string httpClientName)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Written under _gate, but read by every request without taking it.
    private volatile string? _cookie;

    /// <summary>Epoch-ms expiry read off the current pass, for the live-harness report.</summary>
    public DateTimeOffset? PassExpiresAt { get; private set; }

    /// <summary>Whether this session has solved the challenge at least once, for the live-harness report.</summary>
    public bool ChallengeSolved { get; private set; }

    private HttpClient Client => httpClientFactory.CreateClient(httpClientName);

    public async Task<MangaTubeResponse> SendAsync(
        HttpMethod method, string path, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        var response = await SendOnceAsync(method, path, headers, ct);
        if (!IsChallenge(response))
        {
            return response;
        }

        await SolveChallengeAsync(ct);
        response = await SendOnceAsync(method, path, headers, ct);
        if (IsChallenge(response))
        {
            throw new InvalidOperationException($"Manga-Tube challenge solve failed twice in a row for {path}.");
        }

        return response;
    }

    private async Task<MangaTubeResponse> SendOnceAsync(
        HttpMethod method, string path, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (_cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", _cookie);
        }

        if (headers is not null)
        {
            foreach (var (key, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }

        using var response = await Client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        return new MangaTubeResponse(response.StatusCode, response.Content.Headers.ContentType?.MediaType, body);
    }

    // Matches both marker spellings the site (and the Keiyoushi interceptor it was reverse
    // engineered from) has shipped: "window.__challange = {...}" and "_challange = {...}".
    // The latter is a substring of the former, so one pattern covers both.
    private static bool IsChallenge(MangaTubeResponse response) =>
        response.Body.Contains("_challange", StringComparison.Ordinal);

    private async Task SolveChallengeAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // Another caller may have solved it while this one waited for the gate.
            if (_cookie is not null && PassExpiresAt is { } expiry && expiry > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                return;
            }

            var shell = await SendOnceAsync(HttpMethod.Get, "/", null, ct);
            var challenge = ParseChallenge(shell.Body);

            // The page's own script waits ~1s before solving; matched here rather than fired
            // immediately, since the site can tell the two timings apart.
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            var solution = Solve(challenge);

            using var solveRequest = new HttpRequestMessage(HttpMethod.Post, "/");
            var content = new StringContent(string.Empty);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
            solveRequest.Content = content;
            solveRequest.Headers.TryAddWithoutValidation("x-challange-token", challenge.Token);
            solveRequest.Headers.TryAddWithoutValidation("x-challange-arg1", challenge.Arg1);
            solveRequest.Headers.TryAddWithoutValidation("x-challange-arg2", challenge.Arg2);
            solveRequest.Headers.TryAddWithoutValidation("x-challange-arg3", challenge.Arg3);
            solveRequest.Headers.TryAddWithoutValidation("x-challange-arg4", solution);

            using var solveResponse = await Client.SendAsync(solveRequest, ct);
            var mtbpass = solveResponse.Headers.TryGetValues("Set-Cookie", out var setCookieValues)
                ? setCookieValues.FirstOrDefault(v => v.Contains("__mtbpass=", StringComparison.Ordinal))
                : null;
            if (mtbpass is null)
            {
                throw new InvalidOperationException("Manga-Tube challenge solve did not return a __mtbpass cookie.");
            }

            _cookie = mtbpass.Split(';')[0].Trim();
            PassExpiresAt = ParseExpiry(_cookie);
            ChallengeSolved = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary><c>__mtbpass={expiryEpochMs}:{hash}</c>, and only the prefix is ours to read.</summary>
    private static DateTimeOffset? ParseExpiry(string cookie)
    {
        var eq = cookie.IndexOf('=');
        var value = eq >= 0 ? cookie[(eq + 1)..] : cookie;
        var colon = value.IndexOf(':');
        var expiryPart = colon >= 0 ? value[..colon] : value;
        return long.TryParse(expiryPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epochMs)
            ? DateTimeOffset.FromUnixTimeMilliseconds(epochMs)
            : null;
    }

    internal static MangaTubeChallenge ParseChallenge(string html)
    {
        var match = ChallengeObjectPattern().Match(html);
        if (!match.Success)
        {
            throw new InvalidOperationException("Manga-Tube challenge marker not found in response.");
        }

        using var doc = JsonDocument.Parse(match.Groups[1].Value);
        var root = doc.RootElement;
        var arg3 = root.GetProperty("arg3").GetString()
            ?? throw new InvalidOperationException("Manga-Tube challenge is missing arg3.");
        if (arg3 is not ("a" or "b" or "c" or "d"))
        {
            throw new InvalidOperationException($"Manga-Tube challenge has an unrecognized op '{arg3}'.");
        }

        return new MangaTubeChallenge(
            root.GetProperty("arg1").GetString() ?? throw new InvalidOperationException("Manga-Tube challenge is missing arg1."),
            root.GetProperty("arg2").GetString() ?? throw new InvalidOperationException("Manga-Tube challenge is missing arg2."),
            arg3,
            root.GetProperty("tk").GetString() ?? throw new InvalidOperationException("Manga-Tube challenge is missing tk."));
    }

    /// <summary>
    /// arg1/arg2 are hex integers, arg3 picks the op, and the answer must be formatted exactly
    /// as JavaScript's <c>Number.prototype.toString()</c> would: an integral result as a bare
    /// integer, otherwise the shortest round-tripping decimal. <c>double.ToString("R")</c>
    /// matches that at these magnitudes.
    /// </summary>
    internal static string Solve(MangaTubeChallenge challenge)
    {
        var a = Convert.ToInt64(challenge.Arg1, 16);
        var b = Convert.ToInt64(challenge.Arg2, 16);
        double result = challenge.Arg3 switch
        {
            "a" => (double)a / b,
            "b" => (double)(a * b),
            "c" => (double)(a - b),
            "d" => (double)(a + b),
            _ => throw new InvalidOperationException($"Manga-Tube challenge has an unrecognized op '{challenge.Arg3}'.")
        };

        return FormatNumber(result);
    }

    internal static string FormatNumber(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"_challange\s*=\s*(\{[^;]*\})")]
    private static partial Regex ChallengeObjectPattern();
}
