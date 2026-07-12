using Mangarr.Api.Configuration;
using Mangarr.Core.Metadata;
using Mangarr.Core.Sources;
using Microsoft.AspNetCore.Mvc;

namespace Mangarr.Api.Controllers;

[ApiController]
[Route("api/v1/search")]
public class SearchController(
    IEnumerable<IMetadataProvider> metadataProviders,
    SourceRegistry sourceRegistry,
    IHttpClientFactory httpClientFactory,
    ConfigFileProvider configFile) : ControllerBase
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

    /// <summary>Fetches a source cover with the source's Referer so <img> tags can display it.</summary>
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

        var client = httpClientFactory.CreateClient("covers");
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.Referrer = new Uri($"{source.BaseUrl}/");

        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return StatusCode((int)response.StatusCode);
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        Response.Headers.CacheControl = "public,max-age=86400";
        return File(bytes, response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
    }

    [HttpGet("sources")]
    public IActionResult ListSources()
    {
        return Ok(sourceRegistry.All.Select(s => new
        {
            s.Name,
            s.DisplayName,
            s.BaseUrl,
            NeedsFlareSolverr = s.Capabilities.HasFlag(SourceCapabilities.NeedsFlareSolverr)
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

    // The apikey goes in the query string because these URLs land in <img src>,
    // which can't send the X-Api-Key header. The response already requires the key,
    // so this reveals nothing the caller doesn't have.
    private string? ProxiedCoverUrl(string sourceName, string? coverUrl) =>
        coverUrl is null
            ? null
            : $"/api/v1/search/cover?sourceName={Uri.EscapeDataString(sourceName)}" +
              $"&url={Uri.EscapeDataString(coverUrl)}&apikey={configFile.Config.ApiKey}";
}
