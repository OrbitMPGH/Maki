using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Maki.Core.Sources;

namespace Maki.Sources.Manhuagui;

/// <summary>
/// Manhuagui (漫画柜) — Simplified Chinese manhua/manga aggregator, plain HTTP, no Cloudflare.
/// The site bans IPs on bulk reads, so its named client (registered in Program.cs) carries a
/// strict standalone limiter rather than sharing the standard batch's rate.
/// <para>
/// Series id is the numeric comic id ("1128"); chapter id is the numeric chapter id ("909042").
/// The chapter list mixes two kinds of row: numbered chapters ("单话") and numbered volumes
/// ("单行本"), which together cover the whole run and are kept as separate rows (a volume has
/// <see cref="SourceChapter.Number"/> null and <see cref="SourceChapter.Volume"/> set, and vice
/// versa) — see <see cref="ClassifyEntry"/>.
/// </para>
/// <para>
/// A chapter page embeds its image list as a Dean Edwards "packer" call rather than plain
/// markup; <see cref="PackedScript"/> and <see cref="LzString"/> unpack it without a JS engine.
/// </para>
/// </summary>
public partial class ManhuaguiSource(IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-manhuagui";

    private static readonly HtmlParser Parser = new();

    private static readonly string[] SkippedSectionMarkers = ["番外", "外传", "外傳"];

    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "manhuagui.com", "www.manhuagui.com", "tw.manhuagui.com", "m.manhuagui.com",
        "mhgui.com", "www.mhgui.com", "tw.mhgui.com", "m.mhgui.com"
    };

    public string Name => "manhuagui";
    public string DisplayName => "Manhuagui";

    public string BaseUrl =>
        Environment.GetEnvironmentVariable("MAKI_SOURCE_MANHUAGUI_BASEURL")?.TrimEnd('/') ?? "https://www.manhuagui.com";

    public SourceCapabilities Capabilities => SourceCapabilities.None;
    public SourceContent Content => SourceContent.Manhua;

    // BaoziManhuaSource already publishes zh-Hans; keeping the same tag stops a series linked to
    // both sources from splitting into two languages.
    public IReadOnlyList<string> SupportedLanguages => ["zh-Hans"];

    // Covers and page images both live on the CDN host, not on BaseUrl's own domain.
    public IReadOnlyList<string> CoverHosts => ["mhgui.com"];

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    [GeneratedRegex(@"^/comic/(\d+)/?$")]
    private static partial Regex SeriesPathRegex();

    [GeneratedRegex(@"第?\s*(\d+(?:\.\d+)?)\s*[话話回]")]
    private static partial Regex ChapterMarkerRegex();

    [GeneratedRegex(@"第?\s*(\d+)\s*[卷巻]")]
    private static partial Regex VolumeMarkerRegex();

    public string? ResolveSeriesIdFromUrl(Uri url)
    {
        if (!AllowedHosts.Contains(url.Host)
            && SourceUrl.PathTail(url, BaseUrl, "/comic/", firstSegmentOnly: true) is null)
        {
            return null;
        }

        var match = SeriesPathRegex().Match(url.AbsolutePath);
        return match.Success ? match.Groups[1].Value : null;
    }

    // ── Search ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var html = await Client.GetStringAsync($"s/{Uri.EscapeDataString(title)}_p1.html", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var results = new List<SourceSeriesResult>();
        foreach (var item in doc.QuerySelectorAll("div.book-result > ul > li"))
        {
            var link = item.QuerySelector("div.book-detail dl > dt > a[href][title]");
            var href = link?.GetAttribute("href");
            var name = link?.GetAttribute("title");
            if (string.IsNullOrEmpty(href) || string.IsNullOrEmpty(name))
            {
                continue;
            }

            var seriesId = SourceUrl.PathTail(new Uri(BaseUrl + href), BaseUrl, "/comic/", firstSegmentOnly: true);
            if (seriesId is null)
            {
                continue;
            }

            var cover = item.QuerySelector("div.book-cover a.bcover img")?.GetAttribute("src");
            results.Add(new SourceSeriesResult(seriesId, name, $"{BaseUrl}{href}", NormalizeCoverUrl(cover)));
        }

        return results;
    }

    // ── Series detail ─────────────────────────────────────────────────

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var doc = await GetSeriesDocumentAsync(sourceSeriesId, ct);

        var title = doc.QuerySelector("div.book-title > h1")?.TextContent.Trim() ?? sourceSeriesId;
        var cover = doc.QuerySelector("p.hcover > img")?.GetAttribute("src");
        var description = (doc.QuerySelector("#intro-all")?.TextContent
            ?? doc.QuerySelector("#intro-cut")?.TextContent)?.Trim();
        var status = ParseStatus(FirstStatusRedText(doc));

        return new SourceSeriesDetail(
            sourceSeriesId, title, $"{BaseUrl}/comic/{sourceSeriesId}/", NormalizeCoverUrl(cover), description, status);
    }

    // ── Chapters ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var doc = await GetSeriesDocumentAsync(sourceSeriesId, ct);

        var statusLi = doc.QuerySelector("li.status");
        var reds = statusLi?.QuerySelectorAll("span.red").ToList();
        var latestHref = statusLi?.QuerySelector("a.blue")?.GetAttribute("href");
        var latestDateText = reds is { Count: > 1 } ? reds[1].TextContent.Trim() : null;
        DateTime? latestDate = latestDateText is not null &&
            DateTime.TryParseExact(latestDateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate)
                ? parsedDate
                : null;

        var chapters = new List<SourceChapter>();
        var chapterDiv = doc.QuerySelector("div.chapter");
        var headings = chapterDiv?.Children.Where(c => c.TagName.Equals("h4", StringComparison.OrdinalIgnoreCase))
            ?? Enumerable.Empty<IElement>();
        foreach (var heading in headings)
        {
            var sectionName = heading.QuerySelector("span")?.TextContent.Trim() ?? "";
            var listContainer = FindChapterListContainer(heading);
            if (listContainer is null || SkippedSectionMarkers.Any(sectionName.Contains))
            {
                continue;
            }

            foreach (var link in listContainer.QuerySelectorAll("ul > li > a.status0"))
            {
                var href = link.GetAttribute("href");
                var label = link.GetAttribute("title") ?? link.TextContent.Trim();
                if (string.IsNullOrEmpty(href))
                {
                    continue;
                }

                var (number, volume) = ClassifyEntry(label);
                if (number is null && volume is null)
                {
                    continue;
                }

                var chapterId = System.IO.Path.GetFileNameWithoutExtension(href);
                var isLatest = href.Equals(latestHref, StringComparison.Ordinal);

                chapters.Add(new SourceChapter(
                    Name,
                    sourceSeriesId,
                    chapterId,
                    label,
                    number,
                    volume,
                    Title: label,
                    Language: "zh-Hans",
                    ReleaseDate: isLatest ? latestDate : null,
                    Url: $"{BaseUrl}{href}"));
            }
        }

        return SourceChapterList.Normalize(chapters);
    }

    // ── Pages ─────────────────────────────────────────────────────────

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var html = await Client.GetStringAsync($"comic/{chapter.SourceSeriesId}/{chapter.SourceChapterId}.html", ct);
        var data = PackedScript.Unpack(html);

        if (data.Files.Count == 0)
        {
            throw new ChapterLockedException(
                $"manhuagui chapter {chapter.SourceChapterId} of {chapter.SourceSeriesId} has no pages");
        }

        var encodedPath = "/" + string.Join('/',
            data.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString)) + "/";

        var headers = new Dictionary<string, string> { ["Referer"] = "https://www.manhuagui.com/" };
        var pages = data.Files
            .Select(file => new PageRequest(
                $"https://i.hamreus.com{encodedPath}{file}?e={data.Sl.E}&m={data.Sl.M}", headers))
            .ToList();

        return new ChapterPages(pages);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Fetches and parses <c>/comic/{id}/</c>. When the series is behind an audit/R18 gate, the
    /// real chapter markup ships lz-string-compressed in <c>input#__VIEWSTATE</c> instead of the
    /// visible (empty) <c>#erroraudit_show</c> placeholder; this decompresses it and treats that
    /// placeholder as the chapter-list container so the rest of the parsing is unaffected. Not
    /// observed live (One Piece isn't gated) — see the source plan.
    /// </summary>
    private async Task<IHtmlDocument> GetSeriesDocumentAsync(string sourceSeriesId, CancellationToken ct)
    {
        var html = await Client.GetStringAsync($"comic/{sourceSeriesId}/", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var viewState = doc.QuerySelector("input#__VIEWSTATE")?.GetAttribute("value");
        if (string.IsNullOrEmpty(viewState))
        {
            return doc;
        }

        var decompressed = LzString.DecompressFromBase64(viewState);
        var target = doc.QuerySelector("#erroraudit_show");
        if (!string.IsNullOrEmpty(decompressed) && target is not null)
        {
            var hiddenDoc = await Parser.ParseDocumentAsync(decompressed, ct);
            target.InnerHtml = hiddenDoc.Body?.InnerHtml ?? decompressed;
        }

        return doc;
    }

    /// <summary>
    /// Walks forward from an <c>h4</c> section heading past the optional <c>chapter-page</c> range
    /// tabs to find its chapter list. Never pairs by id: One Piece renders <c>id="chapter-list-2"</c>
    /// on both its "单行本" and "番外篇" sections, so an id lookup would silently drop one of them.
    /// The decompressed audit-gate fragment (see <see cref="GetSeriesDocumentAsync"/>) lands inside
    /// <c>#erroraudit_show</c> rather than growing a <c>chapter-list</c> class of its own, so that id
    /// is accepted as a container too.
    /// </summary>
    private static IElement? FindChapterListContainer(IElement heading)
    {
        for (var sibling = heading.NextElementSibling; sibling is not null; sibling = sibling.NextElementSibling)
        {
            if (sibling.TagName.Equals("h4", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (sibling.ClassList.Contains("chapter-list") || sibling.Id == "erroraudit_show")
            {
                return sibling;
            }
        }

        return null;
    }

    /// <summary>
    /// Classifies a chapter-list entry's title text: a "第N话/話/回" chapter, a "第N卷/巻" volume,
    /// or neither (announcements, "短篇", "附录" extras), which the caller skips entirely.
    /// </summary>
    private static (decimal? Number, int? Volume) ClassifyEntry(string label)
    {
        var chapterMatch = ChapterMarkerRegex().Match(label);
        if (chapterMatch.Success)
        {
            return (decimal.Parse(chapterMatch.Groups[1].Value, CultureInfo.InvariantCulture), null);
        }

        var volumeMatch = VolumeMarkerRegex().Match(label);
        if (volumeMatch.Success)
        {
            return (null, int.Parse(volumeMatch.Groups[1].Value, CultureInfo.InvariantCulture));
        }

        return (null, null);
    }

    private static string? FirstStatusRedText(IHtmlDocument doc) =>
        doc.QuerySelector("li.status span.red")?.TextContent.Trim();

    private static string? ParseStatus(string? statusText) => statusText switch
    {
        "连载中" or "連載中" => "Ongoing",
        "已完结" or "已完結" => "Completed",
        _ => null
    };

    private static string? NormalizeCoverUrl(string? url) =>
        url is null ? null : url.StartsWith("//", StringComparison.Ordinal) ? $"https:{url}" : url;
}
