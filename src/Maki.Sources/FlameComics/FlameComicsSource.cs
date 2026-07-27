using System.Globalization;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Maki.Core.Parsing;
using Maki.Core.Sources;

namespace Maki.Sources.FlameComics;

/// <summary>
/// Flame Comics scraper — a scanlation group, mostly manhwa/manhua with some manga.
/// <para>
/// The site is a Next.js Pages Router app, so every page ships its server props as JSON in a
/// <c>#__NEXT_DATA__</c> script tag. That is what we read: it is the same data the page renders
/// from, so there is no markup to keep up with. The <c>/_next/data/{buildId}/…</c> JSON endpoints
/// would be tidier still, but <c>buildId</c> changes on every deploy and would need discovering
/// first — the HTML page needs no such round-trip.
/// </para>
/// <para>
/// There is no search endpoint; <c>/browse</c> ships the entire catalog (~157 titles) in one
/// response, so it is cached and matched by title like TCB Scans and MANGA Plus. Series id is the
/// numeric <c>series_id</c>; chapter id is the chapter's <c>token</c>, which is all
/// <c>/series/{seriesId}/{token}</c> needs.
/// </para>
/// </summary>
public class FlameComicsSource(IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-flamecomics";

    /// <summary>Images and covers live on a separate host that needs no Referer.</summary>
    private const string CdnUrl = "https://cdn.flamecomics.xyz";

    private static readonly HtmlParser Parser = new();

    private readonly SourceCatalog _catalog = new(TimeSpan.FromMinutes(30));

    public string Name => "flamecomics";
    public string DisplayName => "Flame Comics";
    public string BaseUrl => "https://flamecomics.xyz";
    public SourceCapabilities Capabilities => SourceCapabilities.None;

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    public string? ResolveSeriesIdFromUrl(Uri url)
    {
        // https://flamecomics.xyz/series/{id}, and a chapter URL of the same series,
        // which is /series/{id}/{token}.
        var id = SourceUrl.PathTail(url, BaseUrl, "/series/", firstSegmentOnly: true);
        return id is not null && id.All(char.IsAsciiDigit) ? id : null;
    }

    public Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default) =>
        _catalog.SearchAsync(title, FetchCatalogAsync, ct);

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var props = await GetPagePropsAsync($"series/{sourceSeriesId}", ct);
        var series = props.TryGetProperty("series", out var s) ? s : default;

        return new SourceSeriesDetail(
            sourceSeriesId,
            String(series, "title") ?? sourceSeriesId,
            $"{BaseUrl}/series/{sourceSeriesId}",
            CoverUrl(sourceSeriesId, series),
            PlainText(String(series, "description")),
            String(series, "status"));
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        // The whole list arrives in one response — no pagination even at 300+ chapters.
        var props = await GetPagePropsAsync($"series/{sourceSeriesId}", ct);
        if (!props.TryGetProperty("chapters", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var chapters = new List<SourceChapter>();
        foreach (var row in rows.EnumerateArray())
        {
            // The token is the chapter's whole address; without one it can't be fetched.
            var token = String(row, "token");
            var numberRaw = String(row, "chapter");
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(numberRaw))
            {
                continue;
            }

            var chapterTitle = String(row, "title");
            chapters.Add(new SourceChapter(
                Name,
                sourceSeriesId,
                token,
                numberRaw,
                // "311.00", and "0.00" for a prologue — a plain decimal, no volumes anywhere.
                ChapterNumberParser.Parse(numberRaw).Number,
                Volume: null,
                Title: string.IsNullOrWhiteSpace(chapterTitle) ? null : chapterTitle,
                Language: "en", // the catalog is English-only
                UnixTime(row, "release_date"),
                Url: $"{BaseUrl}/series/{sourceSeriesId}/{token}"));
        }

        return SourceChapterList.Normalize(chapters);
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var props = await GetPagePropsAsync(
            $"series/{chapter.SourceSeriesId}/{chapter.SourceChapterId}", ct);
        if (!props.TryGetProperty("chapter", out var data) ||
            !data.TryGetProperty("images", out var images) ||
            images.ValueKind != JsonValueKind.Object)
        {
            return new ChapterPages([]);
        }

        // "images" is an object keyed by page index as a string, so it has to be ordered
        // numerically — sorted as text, page 10 lands between 1 and 2. The rendered page also
        // carries the site's own "read on Flame" banners, which is why the list is built from
        // this payload rather than from the <img> tags.
        var seriesId = String(data, "series_id") ?? chapter.SourceSeriesId;
        var stamp = String(data, "edit_time");
        var query = string.IsNullOrEmpty(stamp) ? string.Empty : $"?{stamp}";

        var pages = images.EnumerateObject()
            .Select(page => (
                Index: int.TryParse(page.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                    ? i
                    : int.MaxValue,
                Name: String(page.Value, "name")))
            .Where(page => !string.IsNullOrEmpty(page.Name))
            .OrderBy(page => page.Index)
            .Select(page => new PageRequest(
                $"{CdnUrl}/uploads/images/series/{seriesId}/{chapter.SourceChapterId}/{Uri.EscapeDataString(page.Name!)}{query}"))
            .ToList();

        return new ChapterPages(pages);
    }

    private async Task<List<SourceSeriesResult>> FetchCatalogAsync(CancellationToken ct)
    {
        var props = await GetPagePropsAsync("browse", ct);
        if (!props.TryGetProperty("series", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var catalog = new List<SourceSeriesResult>();
        var seen = new HashSet<string>();

        foreach (var row in rows.EnumerateArray())
        {
            // The catalog also carries the site's prose novels, which key on "novel_id" instead
            // and live under /novels/ with no page images at all. Requiring series_id is what
            // keeps them out — "Omniscient Reader's Viewpoint" is listed both ways, so without
            // it a search returns a twin that can be linked but never downloaded.
            var id = String(row, "series_id");
            var title = String(row, "title");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title) || !seen.Add(id))
            {
                continue;
            }

            catalog.Add(new SourceSeriesResult(
                id, title, $"{BaseUrl}/series/{id}", CoverUrl(id, row), PlainText(String(row, "description"))));
        }

        return catalog;
    }

    /// <summary>
    /// The <c>props.pageProps</c> object of a page, read out of its <c>#__NEXT_DATA__</c> blob.
    /// </summary>
    private async Task<JsonElement> GetPagePropsAsync(string path, CancellationToken ct)
    {
        var doc = await Parser.ParseDocumentAsync(await Client.GetStringAsync(path, ct), ct);
        var payload = doc.QuerySelector("script#__NEXT_DATA__")?.TextContent;
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new InvalidOperationException($"Flame Comics returned no __NEXT_DATA__ for /{path}");
        }

        using var json = JsonDocument.Parse(payload);
        return json.RootElement.TryGetProperty("props", out var props) &&
               props.TryGetProperty("pageProps", out var pageProps)
            ? pageProps.Clone()
            : default;
    }

    /// <summary>
    /// Covers are "{cover}" filenames relative to the series' CDN folder, cache-busted with the
    /// record's last_edit — the same URL the site's own pages build.
    /// </summary>
    private static string? CoverUrl(string seriesId, JsonElement series)
    {
        var cover = String(series, "cover");
        if (string.IsNullOrEmpty(cover))
        {
            return null;
        }

        var stamp = String(series, "last_edit");
        var query = string.IsNullOrEmpty(stamp) ? string.Empty : $"?{stamp}";
        return $"{CdnUrl}/uploads/images/series/{seriesId}/{Uri.EscapeDataString(cover)}{query}";
    }

    /// <summary>Descriptions are stored as rendered HTML, tags and all.</summary>
    private static string? PlainText(string? html) =>
        string.IsNullOrWhiteSpace(html) ? null : Parser.ParseDocument(html).Body?.TextContent.Trim();

    /// <summary>Reads a property as a string whether the site stored it as one or as a number.</summary>
    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;

    private static DateTime? UnixTime(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(value.GetInt64()).UtcDateTime
            : null;
}
