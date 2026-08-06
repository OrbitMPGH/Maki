using System.Globalization;
using AngleSharp.Html.Parser;
using Maki.Core.Parsing;
using Maki.Core.Sources;

namespace Maki.Sources.TopManhua;

public class TopManhuaSource(IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-topmanhua";
    private static readonly HtmlParser Parser = new();
    public string Name => "topmanhua";
    public string DisplayName => "TopManhua";
    public string BaseUrl => "https://www.topmanhua.fan";
    public SourceCapabilities Capabilities => SourceCapabilities.None;
    public IReadOnlyList<string> CoverHosts => ["2xstorage.com", "zinmanga1.com"];
    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);
    
    public string? ResolveSeriesIdFromUrl(Uri url) =>
    
        // https://www.topmanhua.fan/manhua/{id}
        SourceUrl.PathTail(url, BaseUrl, "/manhua/", firstSegmentOnly: true);
    
    
    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var encodedTitle = Uri.EscapeDataString(title);
        var html = await Client.GetStringAsync($"?s={encodedTitle}&post_type=wp-manga", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);
        
        var results = new List<SourceSeriesResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in doc.QuerySelectorAll(".c-tabs-item > div"))
        {
            var link = item.QuerySelector(".post-title > h3 > a");
            if (link is null)
            {
                continue;
            }
            var href = link.GetAttribute("href")!;
            var path = new Uri(href).AbsolutePath.TrimStart('/');
            var seriesId = path.StartsWith("manhua/", StringComparison.Ordinal)
                ? path["manhua/".Length..]
                : path;
            if (!seen.Add(seriesId))
            {
                continue;
            }
            var titleText = link.HasChildNodes ? link.FirstChild!.TextContent.Trim() : link.TextContent.Trim();
            var cover = item.QuerySelector("img")?.GetAttribute("src");
            
            results.Add(new SourceSeriesResult(seriesId, titleText, href, cover));
        }
        
        return results;
    }

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var html = await Client.GetStringAsync($"manhua/{sourceSeriesId}", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);
        
        var title = doc.QuerySelector(".post-title > h1")?.TextContent.Trim() ?? sourceSeriesId;
        var description = doc.QuerySelector(".summary__content > p:nth-child(2)")?.TextContent.Trim();
        var cover = doc.QuerySelector("div.summary_image > a > img")?.GetAttribute("src");
        
        var statusText = doc.QuerySelector("div.post-content_item:nth-child(2)")?.TextContent.Trim();
        var status = statusText switch
        {
            not null when statusText.Contains("Ongoing", StringComparison.OrdinalIgnoreCase) => "Ongoing",
            not null when statusText.Contains("Completed", StringComparison.OrdinalIgnoreCase) => "Completed",
            _ => null
        };
        
        return new SourceSeriesDetail(sourceSeriesId, title, $"{BaseUrl}/manhua/{sourceSeriesId}", cover, description, status);
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var html = await Client.GetStringAsync($"manhua/{sourceSeriesId}", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var chapters = new List<SourceChapter>();
        foreach (var row in doc.QuerySelectorAll(".chapter-list > div"))
        {
            var link = row.QuerySelector("a");
            if (link is null)
            {
                continue;
            }

            var href = link.GetAttribute("href")!;
            var beforeC = href.LastIndexOf("/chapter-", StringComparison.Ordinal);
            var chapterId = beforeC >= 0 ? href[(beforeC + 1)..] : href;
            
            var label = link.TextContent.Trim();
            var parsed = ChapterNumberParser.Parse(label);
            
            var dateText = row.QuerySelector(".chapter-release-date")?.TextContent.Trim();
            DateTime? releaseDate = null;
            if (dateText is not null
                && DateTime.TryParse(dateText, null, DateTimeStyles.AdjustToUniversal, out var d))
            {
                releaseDate = d;
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
                Url: href));
        }
        return SourceChapterList.Normalize(chapters);
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var html = await Client.GetStringAsync($"manhua/{chapter.SourceSeriesId}/{chapter.SourceChapterId}", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);
        var headers = new Dictionary<string, string> { ["Referer"] = "https://www.topmanhua.fan/" };
        var pages = doc.QuerySelectorAll(".reading-content > div > img")
            .Select(img => img.GetAttribute("data-src"))
            .Where(url => !string.IsNullOrEmpty(url))
            .Select(url => new PageRequest(url!, headers))
            .ToList();
        
        return new ChapterPages(pages);
    }
}