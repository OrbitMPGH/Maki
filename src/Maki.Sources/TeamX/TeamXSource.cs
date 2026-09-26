using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Maki.Core.Http;
using Maki.Core.Parsing;
using Maki.Core.Sources;

namespace Maki.Sources.TeamX;

/// <summary>
/// Team-X scraper (Arabic scanlation group, Laravel-rendered HTML). Cloudflare sits in front of
/// the site but is passive today, so it takes <see cref="IHtmlFetcher"/> anyway per the plan's
/// Cloudflare-tier call. Series ids are the slug as linked, case preserved ("SL", "SMN") since the
/// site's own links are mixed case. The chapter list pages 40 rows at a time; the walk reads the
/// highest page number off the pager and fetches pages 2..last sequentially.
/// </summary>
public partial class TeamXSource(IHtmlFetcher fetcher) : ISource
{
    private static readonly HtmlParser Parser = new();

    public string Name => "teamx";
    public string DisplayName => "Team-X";

    public string BaseUrl =>
        Environment.GetEnvironmentVariable("MAKI_SOURCE_TEAMX_BASEURL")?.TrimEnd('/') ?? "https://olympustaff.com";

    public SourceCapabilities Capabilities => SourceCapabilities.NeedsFlareSolverr;
    public IReadOnlyList<string> SupportedLanguages => ["ar"];

    // Covers and page images are served from BaseUrl's own host.
    public IReadOnlyList<string> CoverHosts => [];

    [GeneratedRegex(@"^\s*الفصل\s+(رقم\s+)?0*\d+(\.\d+)?\s*$")]
    private static partial Regex InvalidChapterTitle();

    public string? ResolveSeriesIdFromUrl(Uri url)
    {
        var tail = SourceUrl.PathTail(url, BaseUrl, "/series/");
        return tail is not null && !tail.Contains('/') ? tail : null;
    }

    // ── Search ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/search?keyword={Uri.EscapeDataString(title)}";
        var html = await fetcher.GetHtmlAsync(url, ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var results = new List<SourceSeriesResult>();
        foreach (var card in doc.QuerySelectorAll("div.tx-grid a.tx-card"))
        {
            var href = card.GetAttribute("href");
            var name = card.QuerySelector("h3.tx-card-title")?.TextContent.Trim();
            if (string.IsNullOrEmpty(href) || string.IsNullOrEmpty(name))
            {
                continue;
            }

            var seriesId = SeriesIdFromHref(href);
            if (seriesId is null)
            {
                continue;
            }

            var cover = card.QuerySelector("div.tx-card-poster img")?.GetAttribute("src");
            results.Add(new SourceSeriesResult(seriesId, name, href, FullCover(cover)));
        }

        return results;
    }

    /// <summary>Search thumbnails carry a "thumbnail_" prefix; the full-size cover drops it.</summary>
    private static string? FullCover(string? thumbnail)
    {
        if (string.IsNullOrEmpty(thumbnail))
        {
            return thumbnail;
        }

        var slash = thumbnail.LastIndexOf('/');
        var fileName = slash >= 0 ? thumbnail[(slash + 1)..] : thumbnail;
        if (!fileName.StartsWith("thumbnail_", StringComparison.Ordinal))
        {
            return thumbnail;
        }

        return thumbnail[..(slash + 1)] + fileName["thumbnail_".Length..];
    }

    private string? SeriesIdFromHref(string href) =>
        Uri.TryCreate(href, UriKind.Absolute, out var uri) ? ResolveSeriesIdFromUrl(uri) : null;

    // ── Series detail ─────────────────────────────────────────────────

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var url = SeriesUrl(sourceSeriesId);
        var html = await fetcher.GetHtmlAsync(url, ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);
        RequireSeriesPage(doc, url, html);

        var title = doc.QuerySelector("div.author-info-title h1")?.TextContent.Trim();
        var cover = doc.QuerySelector("div.text-right img")?.GetAttribute("src");

        return new SourceSeriesDetail(
            sourceSeriesId,
            string.IsNullOrEmpty(title) ? sourceSeriesId : title,
            url,
            cover,
            Description(doc),
            Status(doc));
    }

    private static string? Description(IDocument doc)
    {
        var description = doc.QuerySelector("div.review-content")?.TextContent.Trim();
        if (string.IsNullOrEmpty(description))
        {
            description = doc.QuerySelector("div.review-content p")?.TextContent.Trim();
        }

        return string.IsNullOrEmpty(description) ? null : description;
    }

    private static string? Status(IDocument doc)
    {
        foreach (var block in doc.QuerySelectorAll("div.full-list-info"))
        {
            var labels = block.QuerySelectorAll("small");
            if (labels.Length < 2 || !labels[0].TextContent.Contains("الحالة"))
            {
                continue;
            }

            return labels[1].TextContent.Trim() switch
            {
                "مستمرة" or "قادم قريبًا" => "Ongoing",
                "مكتمل" => "Completed",
                _ => null
            };
        }

        return null;
    }

    // ── Chapters ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var firstUrl = SeriesUrl(sourceSeriesId);
        var html = await fetcher.GetHtmlAsync(firstUrl, ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);
        RequireSeriesPage(doc, firstUrl, html);

        var chapters = new List<SourceChapter>(ParseChapterCards(sourceSeriesId, doc));

        var lastPage = 1;
        foreach (var link in doc.QuerySelectorAll("ul.pagination a.page-link"))
        {
            if (int.TryParse(link.TextContent.Trim(), out var page) && page > lastPage)
            {
                lastPage = page;
            }
        }

        for (var page = 2; page <= lastPage; page++)
        {
            var pageUrl = $"{firstUrl}?page={page}";
            var pageHtml = await fetcher.GetHtmlAsync(pageUrl, ct);
            var pageDoc = await Parser.ParseDocumentAsync(pageHtml, ct);
            RequireSeriesPage(pageDoc, pageUrl, pageHtml);

            var pageChapters = ParseChapterCards(sourceSeriesId, pageDoc);
            if (pageChapters.Count == 0)
            {
                break;
            }

            chapters.AddRange(pageChapters);
        }

        return SourceChapterList.Normalize(chapters);
    }

    private List<SourceChapter> ParseChapterCards(string sourceSeriesId, IDocument doc)
    {
        var chapters = new List<SourceChapter>();
        foreach (var card in doc.QuerySelectorAll("div.chapter-card"))
        {
            if (card.QuerySelector("span.locked") is not null)
            {
                continue;
            }

            var href = card.QuerySelector("a.chapter-link")?.GetAttribute("href");
            var numberRaw = card.GetAttribute("data-number");
            if (string.IsNullOrEmpty(href) || string.IsNullOrEmpty(numberRaw))
            {
                continue;
            }

            var chapterId = href[(href.LastIndexOf('/') + 1)..];
            var parsed = ChapterNumberParser.Parse(numberRaw);

            DateTime? releaseDate = long.TryParse(card.GetAttribute("data-date"), out var unixSeconds)
                ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime
                : null;

            var title = ChapterTitle(card.QuerySelector("div.chapter-title")?.TextContent, numberRaw);

            chapters.Add(new SourceChapter(
                Name, sourceSeriesId, chapterId, numberRaw, parsed.Number, parsed.Volume,
                title, "ar", releaseDate, href));
        }

        return chapters;
    }

    /// <summary>
    /// The Arabic chapter title is kept as-is unless it is a bare restatement of the chapter number
    /// ("الفصل {n}"/"الفصل رقم {n}", or literally the raw number): those carry no information a
    /// user needs, so they become null rather than an invented English placeholder.
    /// </summary>
    private static string? ChapterTitle(string? raw, string numberRaw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var title = raw.Trim().TrimEnd('‏').Trim();
        if (title.Length == 0 || title == numberRaw || InvalidChapterTitle().IsMatch(title))
        {
            return null;
        }

        return title;
    }

    // ── Page images ───────────────────────────────────────────────────

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var url = $"{SeriesUrl(chapter.SourceSeriesId)}/{chapter.SourceChapterId}";
        var html = await fetcher.GetHtmlAsync(url, ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var imageList = doc.QuerySelector("div.image_list");
        if (imageList is null)
        {
            var snippet = html.Length > 100 ? html[..100] : html;
            throw new InvalidOperationException(
                $"Team-X chapter page did not look like a chapter page: {url} ({snippet})");
        }

        var headers = new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" };
        var pages = new List<PageRequest>();
        // Both selectors are scoped to div.image_list; the site uses one element kind per chapter,
        // but the combined selector keeps whichever is present in document order.
        foreach (var element in imageList.QuerySelectorAll("img.manga-chapter-img, canvas[data-src]"))
        {
            var src = element.LocalName == "canvas" ? element.GetAttribute("data-src") : element.GetAttribute("src");
            if (!string.IsNullOrEmpty(src))
            {
                pages.Add(new PageRequest(src, headers));
            }
        }

        if (pages.Count == 0)
        {
            // div.image_list exists but holds neither element kind: a not-yet-released or paid
            // chapter the site still lists, not a broken page. Never hand back zero pages for a
            // listed chapter; the download pipeline retries a locked chapter instead of failing it
            // permanently.
            throw new ChapterLockedException($"Team-X chapter {url} has no images yet");
        }

        return new ChapterPages(pages);
    }

    private static void RequireSeriesPage(IDocument doc, string url, string html)
    {
        if (doc.QuerySelector("div.author-info-title h1") is not null)
        {
            return;
        }

        var snippet = html.Length > 100 ? html[..100] : html;
        throw new InvalidOperationException($"Team-X series page did not look like a series page: {url} ({snippet})");
    }

    private string SeriesUrl(string sourceSeriesId) => $"{BaseUrl}/series/{sourceSeriesId}";
}
