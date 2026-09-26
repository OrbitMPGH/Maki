using System.Globalization;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Maki.Core.Http;
using Maki.Core.Parsing;
using Maki.Core.Sources;

namespace Maki.Sources.Olympus;

/// <summary>
/// Olympus Scanlation scraper. Spanish manhwa/manhua, JSON API behind Cloudflare, so every
/// request goes through <see cref="IHtmlFetcher"/>. The catalog slug is not a stable id (slugs
/// grow a date/time suffix on rotation and the old one 404s), so <see cref="SourceSeriesId"/> is
/// the numeric series id and every other call first resolves it to the current slug through a
/// cached catalog (<see cref="SourceCatalog"/>), rebuilding the id-to-slug map on every fetch.
/// Chapter lists live on a second host, "panel." + the series host, derived from
/// <see cref="BaseUrl"/> rather than a second env var.
/// </summary>
public class OlympusSource(IHtmlFetcher fetcher) : ISource
{
    private static readonly HtmlParser Parser = new();

    private readonly SourceCatalog _catalog = new(TimeSpan.FromMinutes(30));

    /// <summary>Id-to-slug map, rebuilt every time the catalog is fetched (warm or forced).</summary>
    private volatile IReadOnlyDictionary<string, string> _slugById = new Dictionary<string, string>();

    public string Name => "olympus";
    public string DisplayName => "Olympus Scanlation";

    public string BaseUrl =>
        Environment.GetEnvironmentVariable("MAKI_SOURCE_OLYMPUS_BASEURL")?.TrimEnd('/') ?? "https://olympusxyz.com";

    public SourceCapabilities Capabilities => SourceCapabilities.NeedsFlareSolverr;
    public IReadOnlyList<string> SupportedLanguages => ["es"];

    /// <summary>Covers and pages are on media.imagesolymp.xyz, a suffix of this host.</summary>
    public IReadOnlyList<string> CoverHosts => ["imagesolymp.xyz"];

    private string PanelBaseUrl
    {
        get
        {
            var uri = new Uri(BaseUrl);
            return $"{uri.Scheme}://panel.{uri.Host}";
        }
    }

    public string? ResolveSeriesIdFromUrl(Uri url)
    {
        var tail = SourceUrl.PathTail(url, BaseUrl, "/series/", firstSegmentOnly: true);
        if (tail is null || !tail.StartsWith("comic-", StringComparison.Ordinal))
        {
            return null;
        }

        var slug = tail["comic-".Length..];
        foreach (var (id, candidate) in _slugById)
        {
            if (candidate == slug)
            {
                return id;
            }
        }

        // Catalog is cold (nothing searched or linked yet this process). Synchronous by
        // interface contract, so it can't warm itself here; the caller searches first.
        return null;
    }

    public Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default) =>
        _catalog.SearchAsync(title, FetchCatalogAsync, ct);

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var (data, slug) = await FetchSeriesAsync(sourceSeriesId, ct);

        var title = data.TryGetProperty("name", out var n) ? n.GetString() ?? sourceSeriesId : sourceSeriesId;
        var summary = data.TryGetProperty("summary", out var sm) ? sm.GetString() : null;
        var cover = data.TryGetProperty("cover", out var c) ? c.GetString() : null;
        var status = data.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Object &&
                     st.TryGetProperty("name", out var sn)
            ? sn.GetString()
            : null;

        return new SourceSeriesDetail(sourceSeriesId, title, $"{BaseUrl}/series/comic-{slug}", cover, summary, status);
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var slug = await SlugForIdAsync(sourceSeriesId, ct);
        var first = await GetJsonAsync(ChaptersUrl(slug, 1), ct);
        if (IsError(first))
        {
            slug = await SlugForIdAsync(sourceSeriesId, ct, forceRefresh: true);
            first = await GetJsonAsync(ChaptersUrl(slug, 1), ct);
            if (IsError(first))
            {
                throw new InvalidOperationException($"Olympus series {sourceSeriesId} not found after catalog refresh");
            }
        }

        var chapters = new List<SourceChapter>();
        AddChapters(chapters, sourceSeriesId, slug, first);

        var lastPage = first.TryGetProperty("meta", out var meta) &&
                       meta.TryGetProperty("last_page", out var lp) &&
                       lp.ValueKind == JsonValueKind.Number
            ? lp.GetInt32()
            : 1;

        // The slug was just confirmed live by the page-1 fetch above; no retry needed for the rest.
        for (var page = 2; page <= lastPage; page++)
        {
            var json = await GetJsonAsync(ChaptersUrl(slug, page), ct);
            AddChapters(chapters, sourceSeriesId, slug, json);
        }

        return SourceChapterList.Normalize(chapters);
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        // Verified live: the slug segment of this endpoint is ignored (a stale or placeholder
        // slug returns the same chapter body), so no catalog lookup is needed just to fetch pages.
        var json = await GetJsonAsync($"{BaseUrl}/api/capitulo/comic-x/{chapter.SourceChapterId}", ct);

        var pages = json.TryGetProperty("chapter", out var chapterEl) &&
                    chapterEl.TryGetProperty("pages", out var pagesEl) &&
                    pagesEl.ValueKind == JsonValueKind.Array
            ? pagesEl.EnumerateArray()
                .Select(p => p.GetString())
                .Where(url => !string.IsNullOrEmpty(url))
                .Select(url => new PageRequest(url!, new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" }))
                .ToList()
            : [];

        if (pages.Count == 0)
        {
            throw new ChapterLockedException($"Olympus chapter {chapter.SourceChapterId} has no pages");
        }

        return new ChapterPages(pages);
    }

    private void AddChapters(List<SourceChapter> chapters, string sourceSeriesId, string slug, JsonElement json)
    {
        if (!json.TryGetProperty("data", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var row in rows.EnumerateArray())
        {
            var name = row.TryGetProperty("name", out var n) ? n.GetString() : null;
            var id = row.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                ? idEl.GetInt64()
                : (long?)null;
            if (string.IsNullOrEmpty(name) || id is null)
            {
                continue;
            }

            var parsed = ChapterNumberParser.Parse(name);
            var releaseDate = row.TryGetProperty("published_at", out var pub) &&
                               pub.ValueKind == JsonValueKind.String &&
                               DateTime.TryParse(
                                   pub.GetString(), CultureInfo.InvariantCulture,
                                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
                ? d
                : (DateTime?)null;

            chapters.Add(new SourceChapter(
                Name,
                sourceSeriesId,
                id.Value.ToString(CultureInfo.InvariantCulture),
                name,
                parsed.Number,
                Volume: null, // the chapter list never carries volume info
                Title: null, // the list has no titles
                Language: "es",
                releaseDate,
                Url: $"{BaseUrl}/capitulo/{id}/comic-{slug}"));
        }
    }

    private string ChaptersUrl(string slug, int page) =>
        $"{PanelBaseUrl}/api/series/{slug}/chapters?page={page}&direction=desc&type=comic";

    private async Task<(JsonElement Data, string Slug)> FetchSeriesAsync(string sourceSeriesId, CancellationToken ct)
    {
        var slug = await SlugForIdAsync(sourceSeriesId, ct);
        var json = await GetJsonAsync($"{BaseUrl}/api/series/{slug}?type=comic", ct);
        if (IsError(json))
        {
            // A 400 (numeric id) or 404 (rotated slug) both come back {"error": true, ...} rather
            // than throwing, since ChallengeAwareFetcher falls through to FlareSolverr instead of
            // surfacing the origin's status code. One forced catalog refresh covers slug rotation.
            slug = await SlugForIdAsync(sourceSeriesId, ct, forceRefresh: true);
            json = await GetJsonAsync($"{BaseUrl}/api/series/{slug}?type=comic", ct);
            if (IsError(json))
            {
                throw new InvalidOperationException($"Olympus series {sourceSeriesId} not found after catalog refresh");
            }
        }

        return (json.GetProperty("data"), slug);
    }

    private async Task<string> SlugForIdAsync(string sourceSeriesId, CancellationToken ct, bool forceRefresh = false)
    {
        if (!forceRefresh && _slugById.TryGetValue(sourceSeriesId, out var cached))
        {
            return cached;
        }

        if (forceRefresh)
        {
            // Bypasses SourceCatalog's own TTL cache; _slugById is rebuilt as a side effect.
            await FetchCatalogAsync(ct);
        }
        else
        {
            await _catalog.LoadAsync(FetchCatalogAsync, ct);
        }

        if (_slugById.TryGetValue(sourceSeriesId, out var slug))
        {
            return slug;
        }

        throw new InvalidOperationException($"Olympus series id {sourceSeriesId} is not in the catalog");
    }

    private async Task<List<SourceSeriesResult>> FetchCatalogAsync(CancellationToken ct)
    {
        var json = await GetJsonAsync($"{BaseUrl}/api/series/list", ct);
        var results = new List<SourceSeriesResult>();
        var slugById = new Dictionary<string, string>();

        if (json.TryGetProperty("data", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                // Keiyoushi filters this same list client-side; 9 of 879 rows today are prose
                // novels (novel_id shape), not comics.
                var type = row.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type != "comic")
                {
                    continue;
                }

                var id = row.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                    ? idEl.GetInt64()
                    : (long?)null;
                var slug = row.TryGetProperty("slug", out var s) ? s.GetString() : null;
                var name = row.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                if (id is null || string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var idString = id.Value.ToString(CultureInfo.InvariantCulture);
                var cover = row.TryGetProperty("cover", out var c) ? c.GetString() : null;
                slugById[idString] = slug;
                results.Add(new SourceSeriesResult(idString, name, $"{BaseUrl}/series/comic-{slug}", cover));
            }
        }

        _slugById = slugById;
        return results;
    }

    private static bool IsError(JsonElement json) =>
        json.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True;

    private async Task<JsonElement> GetJsonAsync(string url, CancellationToken ct)
    {
        var body = await fetcher.GetHtmlAsync(url, ct);
        if (body.TrimStart().StartsWith('<'))
        {
            // FlareSolverr wraps a JSON response in a <pre> tag like a browser's raw-JSON viewer.
            var doc = await Parser.ParseDocumentAsync(body, ct);
            body = doc.QuerySelector("pre")?.TextContent ?? body;
        }

        using var json = JsonDocument.Parse(body);
        return json.RootElement.Clone();
    }
}
