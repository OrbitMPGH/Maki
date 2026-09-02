using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Maki.Core.Parsing;
using Maki.Core.Sources;

namespace Maki.Sources.MangaKatana;

/// <summary>
/// MangaKatana scraper — SSR-rendered, no Cloudflare. Page images are in an inline
/// script array rather than img tags, so we extract them with a regex.
/// </summary>
public partial class MangaKatanaSource(IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-mangakatana";

    private static readonly HtmlParser Parser = new();

    public string Name => "mangakatana";
    public string DisplayName => "MangaKatana";
    public string BaseUrl => "https://mangakatana.com";
    public SourceCapabilities Capabilities => SourceCapabilities.None;

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    private static readonly string[] DateFormats = ["MMM-dd-yyyy"];

    /// <summary>
    /// Normalizes a series ID that may be a full URL (from older/broken search results)
    /// or a clean "{slug}.{id}" value.
    /// </summary>
    private static string NormalizeSeriesId(string id)
    {
        if (!id.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return id;
        }

        if (Uri.TryCreate(id, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath.TrimStart('/');
            if (path.StartsWith("manga/", StringComparison.Ordinal))
            {
                return path["manga/".Length..];
            }

            return path;
        }

        return id;
    }

    public string? ResolveSeriesIdFromUrl(Uri url) =>
        SourceUrl.PathTail(url, BaseUrl, "/manga/");

    // ── Search ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var encodedTitle = Uri.EscapeDataString(title);
        using var response = await Client.GetAsync($"page/1?search={encodedTitle}&search_by=book_name", ct);

        // A search that matches nothing 404s instead of serving an empty result page.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        // A search that matches exactly one title redirects to that series page, so there
        // is no result list to read — build the single hit out of the detail page instead.
        var finalUrl = response.RequestMessage?.RequestUri;
        if (finalUrl is not null && ResolveSeriesIdFromUrl(finalUrl) is { } redirectedId)
        {
            var redirectedTitle = doc.QuerySelector("h1.heading")?.TextContent.Trim() ?? title;
            var redirectedCover = doc.QuerySelector("div.media div.cover img")?.GetAttribute("src");
            return [new SourceSeriesResult(redirectedId, redirectedTitle, finalUrl.ToString(), redirectedCover)];
        }

        var results = new List<SourceSeriesResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in doc.QuerySelectorAll("div#book_list > div.item"))
        {
            var link = item.QuerySelector("div.text > h3 > a");
            if (link is null)
            {
                continue;
            }

            var href = link.GetAttribute("href")!;
            // Search results return full URLs — extract just "/manga/{slug}.{id}".
            var path = new Uri(href).AbsolutePath.TrimStart('/');
            var seriesId = path.StartsWith("manga/", StringComparison.Ordinal)
                ? path["manga/".Length..]
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

    // ── Series detail ─────────────────────────────────────────────────

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        sourceSeriesId = NormalizeSeriesId(sourceSeriesId);
        var html = await Client.GetStringAsync($"manga/{sourceSeriesId}", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var title = doc.QuerySelector("h1.heading")?.TextContent.Trim() ?? sourceSeriesId;
        var description = doc.QuerySelector(".summary > p")?.TextContent.Trim();
        var cover = doc.QuerySelector("div.media div.cover img")?.GetAttribute("src");

        var statusText = doc.QuerySelector(".value.status")?.TextContent.Trim();
        var status = statusText switch
        {
            not null when statusText.Contains("Ongoing", StringComparison.OrdinalIgnoreCase) => "Ongoing",
            not null when statusText.Contains("Completed", StringComparison.OrdinalIgnoreCase) => "Completed",
            _ => null
        };

        return new SourceSeriesDetail(sourceSeriesId, title, $"{BaseUrl}/manga/{sourceSeriesId}", cover, description, status);
    }

    // ── Chapters ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        sourceSeriesId = NormalizeSeriesId(sourceSeriesId);
        var html = await Client.GetStringAsync($"manga/{sourceSeriesId}", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var chapters = new List<SourceChapter>();
        foreach (var row in doc.QuerySelectorAll("tr:has(.chapter)"))
        {
            var link = row.QuerySelector("a");
            if (link is null)
            {
                continue;
            }

            var href = link.GetAttribute("href")!;

            // Chapter id is the c{number} part of the URL.
            var beforeC = href.LastIndexOf("/c", StringComparison.Ordinal);
            var chapterId = beforeC >= 0 ? href[(beforeC + 1)..] : href;

            var label = link.TextContent.Trim();
            var parsed = ChapterNumberParser.Parse(label);

            var dateText = row.QuerySelector(".update_time")?.TextContent.Trim();
            DateTime? releaseDate = null;
            if (dateText is not null
                && DateTime.TryParseExact(dateText, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
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

        // Site lists newest first.
        return SourceChapterList.Normalize(chapters);
    }

    // ── Page images ───────────────────────────────────────────────────

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var seriesId = NormalizeSeriesId(chapter.SourceSeriesId);
        var html = await Client.GetStringAsync($"manga/{seriesId}/{chapter.SourceChapterId}", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var imageScript = doc.QuerySelectorAll("script")
            .FirstOrDefault(s => s.TextContent.Contains("data-src"))
            ?.TextContent;

        if (imageScript is null)
        {
            return new ChapterPages([]);
        }

        // Find the JS array name that holds image URLs.
        // Pattern: data-src['"],\s*(\w+)  — the array variable name follows "data-src".
        var arrayNameMatch = ArrayNameRegex().Match(imageScript);
        if (!arrayNameMatch.Success)
        {
            return new ChapterPages([]);
        }

        var arrayName = arrayNameMatch.Groups[1].Value;

        // Extract the array contents: var {name}=['url1','url2',...]
        var arrayMatch = Regex.Match(imageScript,
            $@"var\s+{Regex.Escape(arrayName)}\s*=\s*\[([^\]]*)]", RegexOptions.Singleline);
        if (!arrayMatch.Success)
        {
            return new ChapterPages([]);
        }

        var headers = new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" };
        var pages = ImageUrlRegex()
            .Matches(arrayMatch.Groups[1].Value)
            .Select(m => m.Groups[1].Value)
            .Where(url => !string.IsNullOrEmpty(url))
            .Select((url, i) => new PageRequest(url, headers))
            .ToList();

        return new ChapterPages(pages);
    }

    [GeneratedRegex(@"data-src['""],\s*(\w+)")]
    private static partial Regex ArrayNameRegex();

    [GeneratedRegex(@"'([^']*)'")]
    private static partial Regex ImageUrlRegex();
}
