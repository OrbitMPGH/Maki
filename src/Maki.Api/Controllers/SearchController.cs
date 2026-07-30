using System.Net;
using Maki.Api.Services;
using Maki.Core.Metadata;
using Maki.Core.Sources;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/search")]
public class SearchController(
    IEnumerable<IMetadataProvider> metadataProviders,
    SourceRegistry sourceRegistry,
    SourceAvailability sourceAvailability,
    IHttpClientFactory httpClientFactory,
    ILogger<SearchController> logger) : ControllerBase
{
    /// <summary>Search a specific site source, for manually linking a series.</summary>
    [HttpGet("source")]
    public async Task<IActionResult> SearchSource(
        [FromQuery] string sourceName, [FromQuery] string query, CancellationToken ct)
    {
        var source = sourceRegistry.Find(sourceName);
        if (source is null)
        {
            return BadRequest(new { error = $"Unknown source: {sourceName}" });
        }

        var results = await source.SearchAsync(query, ct);

        // Source CDNs often block hotlinking (e.g. MangaPill requires its own Referer,
        // which a browser <img> can't send), so covers are rewritten through our proxy.
        return Ok(results.Select(r => r with { CoverUrl = ProxiedCoverUrl(source.Name, r.CoverUrl) }));
    }

    /// <summary>
    /// Resolves a pasted series-page URL to a source + series id, bypassing search.
    /// Fetches the series detail so the UI can show what will be linked.
    /// </summary>
    [HttpGet("resolvesource")]
    public async Task<IActionResult> ResolveSource([FromQuery] string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) ||
            (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest(new { error = "Not a valid http(s) URL" });
        }

        foreach (var source in sourceRegistry.All)
        {
            var seriesId = source.ResolveSeriesIdFromUrl(target);
            if (seriesId is null)
            {
                continue;
            }

            try
            {
                var detail = await source.GetSeriesAsync(seriesId, ct);
                return Ok(new
                {
                    SourceName = source.Name,
                    source.DisplayName,
                    detail.SourceSeriesId,
                    detail.Title,
                    detail.Url,
                    CoverUrl = ProxiedCoverUrl(source.Name, detail.CoverUrl)
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new
                {
                    error = $"URL matched {source.DisplayName} but the series page could not be fetched: {ex.Message}"
                });
            }
        }

        return NotFound(new { error = "No source recognizes this URL" });
    }

    /// <summary>
    /// Fetches a source cover with the source's Referer so <c>&lt;img&gt;</c> tags can display it
    /// (several CDNs hotlink-block every other referrer).
    /// <para>
    /// This is a server-side fetch of a caller-supplied URL — a textbook SSRF primitive — so the host
    /// is checked against the requested source's own domain before anything is sent. Without that,
    /// any authenticated user could aim Maki at <c>http://169.254.169.254/</c> or at a service on the
    /// host's private network and read the response back through this endpoint.
    /// </para>
    /// </summary>
    [HttpGet("cover")]
    public async Task<IActionResult> SourceCover(
        [FromQuery] string sourceName, [FromQuery] string url, CancellationToken ct)
    {
        var source = sourceRegistry.Find(sourceName);
        if (source is null ||
            !Uri.TryCreate(url, UriKind.Absolute, out var target) ||
            (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest();
        }

        if (!CoverHostPolicy.Allows(source, target))
        {
            logger.LogWarning("Blocked cover proxy request for {Host} via source {Source}", target.Host, sourceName);
            return BadRequest(new { error = "That host is not served by this source" });
        }

        var client = httpClientFactory.CreateClient("covers");
        var referer = new Uri($"{source.BaseUrl}/");

        // Redirects are followed by hand so the allowlist can be re-checked at every hop. Letting
        // HttpClient follow them automatically would make the check above decorative: any open
        // redirect on an allowed CDN would bounce the request to an arbitrary host — including one on
        // the server's private network — and hand the response back through this endpoint.
        var current = target;
        for (var hop = 0; hop <= MaxCoverRedirects; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Referrer = referer;

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest &&
                response.Headers.Location is { } location)
            {
                // Relative Locations resolve against the current URL, so the host can only stay the
                // same or change to whatever an absolute Location names — which is then re-checked.
                var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                if (!CoverHostPolicy.Allows(source, next))
                {
                    logger.LogWarning(
                        "Blocked cover proxy redirect to {Host} via source {Source}", next.Host, sourceName);
                    return BadRequest(new { error = "That host is not served by this source" });
                }

                current = next;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                return StatusCode((int)response.StatusCode);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            Response.Headers.CacheControl = "public,max-age=86400";
            return File(bytes, response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
        }

        return BadRequest(new { error = "Too many redirects" });
    }

    /// <summary>Redirect hops the cover proxy will follow before giving up.</summary>
    private const int MaxCoverRedirects = 3;

    [HttpGet("sources")]
    public async Task<IActionResult> ListSources(CancellationToken ct)
    {
        // Enabled is the global switch, not a per-series one: a disabled source can't be
        // linked and none of its existing mappings run, but those mappings keep their flags.
        var disabled = await sourceAvailability.DisabledAsync(ct);
        return Ok(sourceRegistry.All.Select(s => new
        {
            s.Name,
            s.DisplayName,
            s.BaseUrl,
            NeedsFlareSolverr = s.Capabilities.HasFlag(SourceCapabilities.NeedsFlareSolverr),
            Enabled = !disabled.Contains(s.Name, StringComparer.OrdinalIgnoreCase)
        }));
    }

    [HttpGet("metadata")]
    public async Task<IActionResult> SearchMetadata([FromQuery] string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest(new { error = "query is required" });
        }

        var provider = metadataProviders.First();
        var results = await provider.SearchAsync(query, ct);
        return Ok(results);
    }

    // No credential in the URL. These land in <img src>, which cannot send a header — but the
    // request is same-origin, so the browser attaches the session cookie by itself. The instance API
    // key used to be appended here, which put it in the JSON of an ordinary search response and from
    // there into browser history and any proxy log the image request passed through.
    private static string? ProxiedCoverUrl(string sourceName, string? coverUrl) =>
        coverUrl is null
            ? null
            : $"/api/v1/search/cover?sourceName={Uri.EscapeDataString(sourceName)}" +
              $"&url={Uri.EscapeDataString(coverUrl)}";
}
