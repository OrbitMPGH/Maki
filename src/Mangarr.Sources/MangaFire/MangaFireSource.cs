using System.Text.Json;
using AngleSharp.Html.Parser;
using Mangarr.Core.Http;
using Mangarr.Core.Parsing;
using Mangarr.Core.Sources;
using Microsoft.Extensions.Logging;

namespace Mangarr.Sources.MangaFire;

/// <summary>
/// MangaFire scraper. The site sits behind an anti-bot JS challenge, so all HTML
/// goes through ChallengeAwareFetcher (direct-with-cookies first, FlareSolverr on
/// challenge). Chapter data comes from the site's ajax endpoints:
///   /ajax/read/{code}/chapter/{lang}  → chapter list html (data-id per chapter)
///   /ajax/read/chapter/{dataId}       → page image URLs
/// Series id is stored as "{slug}.{code}" (the tail of /manga/ URLs).
/// </summary>
public class MangaFireSource(ChallengeAwareFetcher fetcher, ILogger<MangaFireSource> logger) : ISource
{
    private static readonly HtmlParser Parser = new();

    public string Name => "mangafire";
    public string DisplayName => "MangaFire";
    public string BaseUrl => "https://mangafire.io";
    public SourceCapabilities Capabilities =>
        SourceCapabilities.NeedsFlareSolverr | SourceCapabilities.SupportsLanguageFilter;

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var html = await fetcher.GetHtmlAsync($"{BaseUrl}/filter?keyword={Uri.EscapeDataString(title)}", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var results = new List<SourceSeriesResult>();
        var seen = new HashSet<string>();
        foreach (var link in doc.QuerySelectorAll("a[href^='/manga/']"))
        {
            var href = link.GetAttribute("href")!;
            var seriesId = href["/manga/".Length..].Trim('/');
            var name = link.TextContent.Trim();

            // Series pages are /manga/{slug}.{code}; skip genre links etc.
            if (string.IsNullOrEmpty(name) || !seriesId.Contains('.') || !seen.Add(seriesId))
            {
                continue;
            }

            var container = link.Closest("div.unit") ?? link.ParentElement?.ParentElement;
            var cover = container?.QuerySelector("img")?.GetAttribute("src");

            results.Add(new SourceSeriesResult(seriesId, name, $"{BaseUrl}{href}", cover));
        }

        return results;
    }

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var html = await fetcher.GetHtmlAsync($"{BaseUrl}/manga/{sourceSeriesId}", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var title = doc.QuerySelector("h1")?.TextContent.Trim() ?? sourceSeriesId;
        var description = doc.QuerySelector("#synopsis, .description, .summary")?.TextContent.Trim();
        return new SourceSeriesDetail(sourceSeriesId, title, $"{BaseUrl}/manga/{sourceSeriesId}", null, description);
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var code = CodeFrom(sourceSeriesId);
        var language = string.IsNullOrWhiteSpace(languageFilter) ? "en" : languageFilter;

        var body = await fetcher.GetHtmlAsync($"{BaseUrl}/ajax/read/{code}/chapter/{language}", ct);
        var listHtml = ExtractAjaxResult(body);
        var doc = await Parser.ParseDocumentAsync(listHtml, ct);

        var chapters = new List<SourceChapter>();
        foreach (var link in doc.QuerySelectorAll("a[data-id]"))
        {
            var dataId = link.GetAttribute("data-id")!;
            var label = link.TextContent.Trim();
            var parsed = ChapterNumberParser.Parse(
                link.GetAttribute("data-number") is string n && n.Length > 0 ? n : label);

            chapters.Add(new SourceChapter(
                Name,
                sourceSeriesId,
                dataId,
                label,
                parsed.Number,
                parsed.Volume,
                Title: null,
                Language: language,
                ReleaseDate: null,
                Url: $"{BaseUrl}{link.GetAttribute("href")}"));
        }

        return chapters
            .GroupBy(c => (c.Number, c.Volume))
            .Select(g => g.First())
            .OrderBy(c => c.Number)
            .ToList();
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var body = await fetcher.GetHtmlAsync($"{BaseUrl}/ajax/read/chapter/{chapter.SourceChapterId}", ct);

        using var json = JsonDocument.Parse(body);
        var images = json.RootElement.GetProperty("result").GetProperty("images");

        var headers = fetcher.SessionHeadersFor(new Uri(BaseUrl).Host, $"{BaseUrl}/");
        var pages = new List<PageRequest>();
        foreach (var entry in images.EnumerateArray())
        {
            // Entries are [url, page, scrambleOffset]; offset > 0 means the image
            // is scrambled. Descrambling is not implemented yet — log and continue.
            var url = entry[0].GetString();
            if (url is null)
            {
                continue;
            }

            if (entry.GetArrayLength() > 2 && entry[2].ValueKind == JsonValueKind.Number && entry[2].GetInt32() > 0)
            {
                logger.LogWarning("MangaFire chapter {Id} contains scrambled pages; output may be garbled",
                    chapter.SourceChapterId);
            }

            pages.Add(new PageRequest(url, headers));
        }

        return new ChapterPages(pages);
    }

    private static string CodeFrom(string sourceSeriesId) =>
        sourceSeriesId[(sourceSeriesId.LastIndexOf('.') + 1)..];

    /// <summary>Ajax endpoints wrap html in {"status":200,"result":...}; result may be a string or {html:...}.</summary>
    private static string ExtractAjaxResult(string body)
    {
        using var json = JsonDocument.Parse(body);
        var result = json.RootElement.GetProperty("result");
        return result.ValueKind == JsonValueKind.String
            ? result.GetString()!
            : result.GetProperty("html").GetString()!;
    }
}
