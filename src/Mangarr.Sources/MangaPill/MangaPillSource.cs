using AngleSharp.Html.Parser;
using Mangarr.Core.Parsing;
using Mangarr.Core.Sources;

namespace Mangarr.Sources.MangaPill;

/// <summary>
/// MangaPill scraper (plain HTML, no Cloudflare). Series id is "{numericId}/{slug}"
/// so URLs can be rebuilt. Image CDN requires a mangapill.com Referer.
/// </summary>
public class MangaPillSource(IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-mangapill";

    private static readonly HtmlParser Parser = new();

    public string Name => "mangapill";
    public string DisplayName => "MangaPill";
    public string BaseUrl => "https://mangapill.com";
    public SourceCapabilities Capabilities => SourceCapabilities.None;

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var html = await Client.GetStringAsync($"search?q={Uri.EscapeDataString(title)}", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var results = new List<SourceSeriesResult>();
        foreach (var link in doc.QuerySelectorAll("a.mb-2[href^='/manga/']"))
        {
            var href = link.GetAttribute("href")!;
            var seriesId = href["/manga/".Length..];
            var name = link.TextContent.Trim();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var cover = link.ParentElement?.ParentElement
                ?.QuerySelector("figure img")?.GetAttribute("data-src");

            results.Add(new SourceSeriesResult(seriesId, name, $"{BaseUrl}{href}", cover));
        }

        return results;
    }

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var html = await Client.GetStringAsync($"manga/{sourceSeriesId}", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var title = doc.QuerySelector("h1")?.TextContent.Trim() ?? sourceSeriesId;
        var description = doc.QuerySelector("p.text-sm, p.text-secondary")?.TextContent.Trim();
        return new SourceSeriesDetail(sourceSeriesId, title, $"{BaseUrl}/manga/{sourceSeriesId}", null, description);
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var html = await Client.GetStringAsync($"manga/{sourceSeriesId}", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var chapters = new List<SourceChapter>();
        foreach (var link in doc.QuerySelectorAll("a[href^='/chapters/']"))
        {
            var href = link.GetAttribute("href")!;
            var chapterId = href["/chapters/".Length..];
            var label = (link.GetAttribute("title") ?? link.TextContent).Trim();
            var parsed = ChapterNumberParser.Parse(label);

            chapters.Add(new SourceChapter(
                Name,
                sourceSeriesId,
                chapterId,
                label,
                parsed.Number,
                parsed.Volume,
                Title: null,
                Language: "en", // MangaPill is English-only
                ReleaseDate: null,
                Url: $"{BaseUrl}{href}"));
        }

        // Site lists newest first; normalize to ascending and drop duplicates.
        return chapters
            .GroupBy(c => (c.Number, c.Volume))
            .Select(g => g.First())
            .OrderBy(c => c.Number)
            .ToList();
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var html = await Client.GetStringAsync($"chapters/{chapter.SourceChapterId}", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var headers = new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" };
        var pages = doc.QuerySelectorAll("img.js-page")
            .Select(img => img.GetAttribute("data-src") ?? img.GetAttribute("src"))
            .Where(src => !string.IsNullOrEmpty(src))
            .Select(src => new PageRequest(src!, headers))
            .ToList();

        return new ChapterPages(pages);
    }
}
