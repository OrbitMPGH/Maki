using System.Globalization;
using AngleSharp.Html.Parser;
using Maki.Core.Parsing;
using Maki.Core.Sources;

namespace Maki.Sources.TopManhua;

public class TopManhuaSource(IHttpClientFactory httpClientFactory, TopManhuaImageBrowser imageBrowser) : ISource
{
    public const string HttpClientName = "source-topmanhua";
    private static readonly HtmlParser Parser = new();
    public string Name => "topmanhua";
    public string DisplayName => "TopManhua";
    public string BaseUrl => "https://www.topmanhua.fan";
    // Page images (not search/chapter-list HTML) are fetched through TopManhuaImageBrowser, which
    // needs FlareSolverr to seed a real browser session against the Cloudflare-fronted image CDN.
    public SourceCapabilities Capabilities => SourceCapabilities.NeedsFlareSolverr;
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
        var headers = new Dictionary<string, string>
        {
            ["Referer"] = "https://www.topmanhua.fan/",
            ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:153.0) Gecko/20100101 Firefox/153.0",
            ["Accept"] = "image/avif,image/webp,image/png,image/svg+xml,image/*;q=0.8,*/*;q=0.5",
            ["Accept-Language"] = "en-US,en;q=0.9",
            ["Sec-Fetch-Dest"] = "image",
            ["Sec-Fetch-Mode"] = "no-cors",
            ["Sec-Fetch-Site"] = "cross-site",
            ["Sec-GPC"] = "1",
            ["Priority"] = "u=5, i"
        };
        var urls = doc.QuerySelectorAll(".reading-content > div > img")
            .Select(img => img.GetAttribute("data-src"))
            .Where(url => !string.IsNullOrEmpty(url))
            .Select(url => url!)
            .ToList();

        // Plain requests to the image CDN (img-r2.2xstorage.com) get a Cloudflare bot-management
        // block even with matching headers — it tracks the client's TLS/HTTP2 fingerprint, which a
        // .NET HttpClient can't spoof. Fetch through a real Chromium loading the chapter page
        // instead; any URL it doesn't capture in time falls back to the plain fetch as before.
        var chapterUrl = $"{BaseUrl}/manhua/{chapter.SourceSeriesId}/{chapter.SourceChapterId}";
        var captured = await imageBrowser.FetchImagesAsync(chapterUrl, urls, ct);

        var pages = urls
            .Select(url => captured.TryGetValue(url, out var data)
                ? new PageRequest(url, headers, Data: data)
                : new PageRequest(url, headers))
            .ToList();

        return new ChapterPages(pages);
    }
}