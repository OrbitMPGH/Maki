using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Maki.Core.Parsing;
using Maki.Core.Sources;

namespace Maki.Sources.TCBScans;

/// <summary>
/// TCB Scans scraper — a small, fast scanlation group for major Shonen Jump titles
/// (One Piece, Jujutsu Kaisen, My Hero Academia, Chainsaw Man…). Plain HTML, no
/// Cloudflare. It has no search endpoint, so we match against its full /projects
/// catalog, which is small enough to fetch and cache. Series/chapter ids are the
/// "{numericId}/{slug}" tails of /mangas/ and /chapters/ URLs.
/// </summary>
public partial class TCBScansSource(IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-tcbscans";

    private static readonly HtmlParser Parser = new();

    [GeneratedRegex(@"/mangas/(\d+/[^""']+)")]
    private static partial Regex MangaUrl();

    [GeneratedRegex(@"/chapters/(\d+/[^""']+)")]
    private static partial Regex ChapterUrl();

    private readonly SourceCatalog _catalog = new(TimeSpan.FromMinutes(10));

    public string Name => "tcbscans";
    public string DisplayName => "TCB Scans";
    public string BaseUrl => "https://tcbonepiecechapters.com";
    public SourceCapabilities Capabilities => SourceCapabilities.None;

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    public string? ResolveSeriesIdFromUrl(Uri url) =>
        // https://tcbonepiecechapters.com/mangas/{id}/{slug} — the id spans both segments
        SourceUrl.PathTail(url, BaseUrl, "/mangas/");

    public Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default) =>
        _catalog.SearchAsync(title, FetchCatalogAsync, ct);

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var doc = await GetHtmlAsync($"mangas/{sourceSeriesId}", ct);
        var title = doc.QuerySelector("div.order-1 h1, h1")?.TextContent.Trim() ?? sourceSeriesId;
        var description = doc.QuerySelector("div.order-1 p")?.TextContent.Trim();
        return new SourceSeriesDetail(sourceSeriesId, title, $"{BaseUrl}/mangas/{sourceSeriesId}", null, description);
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var doc = await GetHtmlAsync($"mangas/{sourceSeriesId}", ct);

        var chapters = new List<SourceChapter>();
        foreach (var link in doc.QuerySelectorAll("a[href*='/chapters/']"))
        {
            var match = ChapterUrl().Match(link.GetAttribute("href") ?? string.Empty);
            if (!match.Success)
            {
                continue;
            }

            var chapterId = match.Groups[1].Value; // "7991/one-piece-chapter-1187"
            var label = link.TextContent.Trim();
            var parsed = ChapterNumberParser.Parse(label);

            chapters.Add(new SourceChapter(
                Name,
                sourceSeriesId,
                chapterId,
                label,
                parsed.Number,
                parsed.Volume,
                Title: null,
                Language: "en", // TCB is English-only
                ReleaseDate: null,
                Url: $"{BaseUrl}/chapters/{chapterId}"));
        }

        return SourceChapterList.Normalize(chapters);
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var doc = await GetHtmlAsync($"chapters/{chapter.SourceChapterId}", ct);

        var headers = new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" };
        // Page images carry the fixed-ratio-content class; the site logo does not.
        var pages = doc.QuerySelectorAll("img.fixed-ratio-content")
            .Select(img => img.GetAttribute("src") ?? img.GetAttribute("data-src"))
            .Where(src => !string.IsNullOrEmpty(src) && src!.StartsWith("http", StringComparison.Ordinal))
            .Select(src => new PageRequest(src!, headers))
            .ToList();

        return new ChapterPages(pages);
    }

    private async Task<List<SourceSeriesResult>> FetchCatalogAsync(CancellationToken ct)
    {
        var doc = await GetHtmlAsync("projects", ct);
        var catalog = new List<SourceSeriesResult>();
        var seen = new HashSet<string>();

        foreach (var link in doc.QuerySelectorAll("a[href*='/mangas/']"))
        {
            var match = MangaUrl().Match(link.GetAttribute("href") ?? string.Empty);
            if (!match.Success)
            {
                continue;
            }

            var seriesId = match.Groups[1].Value; // "5/one-piece"
            var title = link.TextContent.Trim();
            if (string.IsNullOrEmpty(title) || !seen.Add(seriesId))
            {
                continue;
            }

            var cover = link.QuerySelector("img")?.GetAttribute("src");
            catalog.Add(new SourceSeriesResult(seriesId, title, $"{BaseUrl}/mangas/{seriesId}", cover));
        }

        return catalog;
    }

    private async Task<AngleSharp.Html.Dom.IHtmlDocument> GetHtmlAsync(string path, CancellationToken ct)
    {
        var html = await Client.GetStringAsync(path, ct);
        return await Parser.ParseDocumentAsync(html, ct);
    }
}
