using System.Net;
using Maki.Api.Localization;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Download;
using Maki.Core.Metadata;
using Maki.Core.Quality;
using Maki.Core.Security;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Controllers;

[ApiController]
[Route("api/v1/search")]
public class SearchController(
    ILocalizer localizer,
    IEnumerable<IMetadataProvider> metadataProviders,
    SourceRegistry sourceRegistry,
    SourceAvailability sourceAvailability,
    ICurrentUser currentUser,
    IHttpClientFactory httpClientFactory,
    IAppSettings settings,
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
            return this.Fail(localizer, "error.search.unknownSource", new { sourceName });
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
            return this.Fail(localizer, "error.search.invalidUrl");
        }

        foreach (var source in sourceRegistry.All)
        {
            var seriesId = await source.ResolveSeriesIdFromUrlAsync(target, ct);
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

        return this.NotFoundMessage(localizer, "error.search.noSourceRecognizesUrl");
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
            return this.Fail(localizer, "error.search.hostNotServed");
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

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                logger.LogDebug(ex, "Cover fetch from {Host} via source {Source} failed", current.Host, sourceName);
                return this.BadGateway(localizer, "error.search.coverUnavailable");
            }

            using var _ = response;

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
                    return this.Fail(localizer, "error.search.hostNotServed");
                }

                current = next;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogDebug(
                    "Cover fetch from {Host} via source {Source} answered {Status}",
                    current.Host, sourceName, (int)response.StatusCode);
                return this.BadGateway(localizer, "error.search.coverUnavailable");
            }

            var bytes = await ReadCappedAsync(response.Content, MaxCoverBytes, ct);
            if (bytes is null)
            {
                logger.LogWarning(
                    "Cover from {Host} via source {Source} exceeds {Max} bytes; refused",
                    current.Host, sourceName, MaxCoverBytes);
                return this.BadGateway(localizer, "error.search.coverUnavailable");
            }

            // The bytes decide the type, never the upstream's Content-Type header. This response is
            // served from Maki's own origin with the reader's session, and the global CSP allows
            // same-origin scripts, so an upstream answering text/html or image/svg+xml would run as
            // Maki. Anything that is not a raster image is refused outright, and the per-response
            // CSP below keeps even a crafted image inert if a browser ever sniffs it differently.
            var mediaType = ImageValidator.SniffMediaType(bytes);
            if (mediaType is null)
            {
                logger.LogWarning(
                    "Cover from {Host} via source {Source} is not an image ({Declared}); refused",
                    current.Host, sourceName, response.Content.Headers.ContentType?.MediaType ?? "no content type");
                return this.BadGateway(localizer, "error.search.notAnImage");
            }

            // private, not public: the endpoint needs a session, so a shared cache in front of Maki
            // must not hand one reader's proxied response to another.
            Response.Headers.CacheControl = "private,max-age=86400";
            Response.Headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
            return File(bytes, mediaType);
        }

        return this.Fail(localizer, "error.search.tooManyRedirects");
    }

    /// <summary>Redirect hops the cover proxy will follow before giving up.</summary>
    private const int MaxCoverRedirects = 3;

    /// <summary>
    /// Ceiling on a proxied cover. Real covers are well under a megabyte; the cap stops a reader from
    /// making the server buffer an arbitrarily large file from an allowed host.
    /// </summary>
    private const int MaxCoverBytes = 10 * 1024 * 1024;

    /// <summary>The whole body, or null once it would exceed <paramref name="max"/> bytes.</summary>
    private static async Task<byte[]?> ReadCappedAsync(HttpContent content, int max, CancellationToken ct)
    {
        if (content.Headers.ContentLength is > 0 and var declared && declared > max)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > max)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>A source's own measurements count once there are this many samples from this many series.</summary>
    private const int LibraryQualityMinSamples = 10;
    private const int LibraryQualityMinSeries = 2;

    [HttpGet("sources")]
    public async Task<IActionResult> ListSources([FromServices] MakiDbContext db, CancellationToken ct)
    {
        // Enabled is the global switch, not a per-series one: a disabled source can't be
        // linked and none of its existing mappings run, but those mappings keep their flags.
        var disabled = await sourceAvailability.DisabledAsync(ct);
        var instanceLanguage = Core.Localization.SupportedLanguages.Resolve(
            await settings.GetAsync(SettingKeys.UiDefaultLanguage, ct));
        var library = await SourceQualitySamples.LibraryEstimatesAsync(db, ct);
        var defaultRank = SourceMatchService.OrderSources(sourceRegistry.All, null)
            .Select((s, i) => (s.Name, i))
            .ToDictionary(x => x.Name, x => x.i, StringComparer.OrdinalIgnoreCase);

        return Ok(sourceRegistry.All.Select(s => new
        {
            s.Name,
            s.DisplayName,
            s.BaseUrl,
            NeedsFlareSolverr = s.Capabilities.HasFlag(SourceCapabilities.NeedsFlareSolverr),
            // Whether ListChaptersAsync honours SourceMapping.LanguageFilter, so the mappings card
            // only offers a language picker where one does something. A multi-language source that
            // serves each language as its own series id (MANGA Plus) answers false: there is
            // nothing to filter, a second language is a second mapping.
            SupportsLanguageFilter = s.Capabilities.HasFlag(SourceCapabilities.SupportsLanguageFilter),
            s.SupportedLanguages,
            Enabled = !disabled.Contains(s.Name, StringComparer.OrdinalIgnoreCase),
            Kind = s.Kind.ToString().ToLowerInvariant(),
            Content = ContentFlagNames(s.Content),
            Rating = s.Rating.ToString().ToLowerInvariant(),
            DefaultEnabled = SourceAvailability.DefaultsOn(s, instanceLanguage),
            // Position in the order a fresh install uses, so "Reset to defaults" restores it.
            DefaultRank = defaultRank[s.Name],
            Quality = QualityOf(s.Name, library)
        }));
    }

    /// <param name="Basis">"library" when this instance's own samples decide it, "baseline" for <see cref="SourceQualityBaseline"/>.</param>
    public record SourceQualitySummary(string Rating, double BitsPerPixel, string Basis, int Samples, int Series);

    /// <summary>Null when neither this library nor the baseline has measured the source.</summary>
    private static SourceQualitySummary? QualityOf(
        string sourceName, Dictionary<string, (SourceQualityEstimate Estimate, int Series)> library)
    {
        if (library.TryGetValue(sourceName, out var own) &&
            own.Estimate.Samples >= LibraryQualityMinSamples && own.Series >= LibraryQualityMinSeries)
        {
            return Summary(own.Estimate.BitsPerPixel, "library", own.Estimate.Samples, own.Series);
        }

        return SourceQualityBaseline.BitsPerPixel.TryGetValue(sourceName, out var baseline)
            ? Summary(baseline, "baseline", 0, 0)
            : null;

        static SourceQualitySummary Summary(double bpp, string basis, int samples, int series) => new(
            SourceQualityBaseline.Rate(bpp).ToString().ToLowerInvariant(), Math.Round(bpp, 2), basis, samples, series);
    }

    /// <summary>Lowercase flag names set on <paramref name="content"/>, in declaration order.</summary>
    private static string[] ContentFlagNames(SourceContent content) =>
        new[]
        {
            (SourceContent.Manga, "manga"),
            (SourceContent.Manhwa, "manhwa"),
            (SourceContent.Manhua, "manhua"),
            (SourceContent.Webtoon, "webtoon"),
            (SourceContent.Doujinshi, "doujinshi")
        }
        .Where(pair => content.HasFlag(pair.Item1))
        .Select(pair => pair.Item2)
        .ToArray();

    [HttpGet("metadata")]
    public async Task<IActionResult> SearchMetadata([FromQuery] string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return this.Fail(localizer, "error.search.queryRequired");
        }

        // The caller's own ceiling, not an instance setting: an account created at "safe" and denied
        // ChangeContentRating is the whole point of the column, and it is the only thing standing
        // between that account and every rating MangaBaka carries.
        var provider = metadataProviders.First();
        var results = await provider.SearchAsync(query, currentUser.MaxContentRating, ct);
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
