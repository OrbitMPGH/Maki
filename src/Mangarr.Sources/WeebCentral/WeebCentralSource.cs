using AngleSharp.Html.Parser;
using Mangarr.Core.Parsing;
using Mangarr.Core.Sources;

namespace Mangarr.Sources.WeebCentral;

/// <summary>
/// Weeb Central scraper. The site is HTMX-driven, so we hit the partial-HTML
/// endpoints directly (search/data, full-chapter-list, chapter images).
/// Series id is stored as "{ULID}/{slug}".
/// </summary>
public class WeebCentralSource(IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-weebcentral";

    private static readonly HtmlParser Parser = new();

    public string Name => "weebcentral";
    public string DisplayName => "Weeb Central";
    public string BaseUrl => "https://weebcentral.com";
    public SourceCapabilities Capabilities => SourceCapabilities.None;

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var html = await Client.GetStringAsync(
            $"search/data?limit=32&offset=0&text={Uri.EscapeDataString(title)}" +
            "&sort=Best%20Match&order=Ascending&official=Any&display_mode=Full%20Display",
            ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        // Each result renders two anchors for the same series: a card wrapper
        // (image + badges) and a clean title link (class "link"). Index covers
        // from the wrappers, take titles from the title links.
        var covers = new Dictionary<string, string>();
        var results = new List<SourceSeriesResult>();
        var seen = new HashSet<string>();

        foreach (var link in doc.QuerySelectorAll("a[href*='/series/']"))
        {
            var seriesId = SeriesIdFrom(link.GetAttribute("href")!);
            var cover = link.QuerySelector("img")?.GetAttribute("src");
            if (cover != null && !covers.ContainsKey(seriesId))
            {
                covers[seriesId] = cover;
            }
        }

        foreach (var link in doc.QuerySelectorAll("a.link[href*='/series/'], a.link-hover[href*='/series/']"))
        {
            var href = link.GetAttribute("href")!;
            var seriesId = SeriesIdFrom(href);
            var name = link.TextContent.Trim();

            if (string.IsNullOrEmpty(name) || !seen.Add(seriesId))
            {
                continue;
            }

            results.Add(new SourceSeriesResult(seriesId, name, href, covers.GetValueOrDefault(seriesId)));
        }

        return results;
    }

    private static string SeriesIdFrom(string href)
    {
        const string marker = "/series/";
        return href[(href.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
    }

    public string? ResolveSeriesIdFromUrl(Uri url) =>
        // https://weebcentral.com/series/{id}/{slug} — the id spans both segments
        SourceUrl.PathTail(url, BaseUrl, "/series/");

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var html = await Client.GetStringAsync($"series/{sourceSeriesId}", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var title = doc.QuerySelector("h1")?.TextContent.Trim() ?? sourceSeriesId;
        var description = doc.QuerySelector("li p.whitespace-pre-wrap, p.whitespace-pre-wrap")?.TextContent.Trim();
        return new SourceSeriesDetail(sourceSeriesId, title, $"{BaseUrl}/series/{sourceSeriesId}", null, description);
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var ulid = sourceSeriesId.Split('/')[0];
        var html = await Client.GetStringAsync($"series/{ulid}/full-chapter-list", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var chapters = new List<SourceChapter>();
        foreach (var link in doc.QuerySelectorAll("a[href*='/chapters/']"))
        {
            var href = link.GetAttribute("href")!;
            var marker = "/chapters/";
            var chapterId = href[(href.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];

            var label = link.QuerySelector("span.grow > span")?.TextContent.Trim()
                        ?? link.TextContent.Trim();
            var parsed = ChapterNumberParser.Parse(label);

            DateTime? releaseDate = null;
            var time = link.QuerySelector("time")?.GetAttribute("datetime");
            if (time != null && DateTime.TryParse(time, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
            {
                releaseDate = dt;
            }

            chapters.Add(new SourceChapter(
                Name,
                sourceSeriesId,
                chapterId,
                label,
                parsed.Number,
                parsed.Volume,
                Title: null,
                Language: "en",
                releaseDate,
                href));
        }

        return SourceChapterList.Normalize(chapters);
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var html = await Client.GetStringAsync(
            $"chapters/{chapter.SourceChapterId}/images?is_prev=False&current_page=1&reading_style=long_strip",
            ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var headers = new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" };
        var pages = doc.QuerySelectorAll("img")
            .Select(img => img.GetAttribute("src"))
            .Where(src => !string.IsNullOrEmpty(src) && src!.StartsWith("http", StringComparison.Ordinal))
            .Select(src => new PageRequest(src!, headers))
            .ToList();

        return new ChapterPages(pages);
    }
}
