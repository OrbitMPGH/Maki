using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Mangarr.Core.Http;

/// <summary>Thin client for a FlareSolverr instance (POST /v1, cmd=request.get).</summary>
public class FlareSolverrClient(IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = "flaresolverr";

    public record FlareSolution(
        int Status,
        string Html,
        string UserAgent,
        IReadOnlyDictionary<string, string> Cookies);

    public async Task<FlareSolution> GetAsync(string flareSolverrUrl, string targetUrl, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var endpoint = flareSolverrUrl.TrimEnd('/') + "/v1";

        var response = await client.PostAsJsonAsync(endpoint, new
        {
            cmd = "request.get",
            url = targetUrl,
            maxTimeout = 60000
        }, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<FlareResponse>(ct)
            ?? throw new InvalidOperationException("FlareSolverr returned an empty response");

        if (body.Status != "ok" || body.Solution is null)
        {
            throw new InvalidOperationException($"FlareSolverr failed: {body.Message ?? body.Status}");
        }

        return new FlareSolution(
            body.Solution.Status,
            body.Solution.Response ?? string.Empty,
            body.Solution.UserAgent ?? string.Empty,
            body.Solution.Cookies.ToDictionary(c => c.Name, c => c.Value));
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
    }
}
