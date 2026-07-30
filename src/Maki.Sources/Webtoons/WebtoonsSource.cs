using System.Globalization;
using AngleSharp.Html.Parser;
using Maki.Core.Sources;

namespace Maki.Sources.Webtoons;

/// <summary>
/// WEBTOON (webtoons.com) scraper — the official Naver/LINE platform, covering both
/// ORIGINALS and reader-published CANVAS titles. English service only (<c>/en/</c>).
/// <para>
/// Plain server-rendered HTML, no Cloudflare. Series id is "{genre}/{slug}/{titleNo}",
/// chapter id is "{episodeNo}|{episodeSlug}" — only the numeric ids actually select
/// anything (a wrong genre/slug redirects to the canonical URL), but carrying the path
/// keeps the links we hand the UI real and saves a redirect hop per fetch. The one
/// exception is CANVAS, whose titles 404 outside the literal <c>canvas</c> segment,
/// which is why the path is stored rather than rebuilt from a placeholder.
/// </para>
/// </summary>
public class WebtoonsSource(IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-webtoons";

    /// <summary>
    /// Episode lists page 10 entries at a time and there is no bulk endpoint, so a long
    /// series costs one request per 10 episodes. An out-of-range page silently clamps to
    /// the last one (and the "next" arrow is rendered even there), so the walk stops on
    /// the first page that adds no new episode — this cap is only a runaway guard.
    /// </summary>
    private const int MaxListPages = 500;

    private static readonly HtmlParser Parser = new();

    public string Name => "webtoons";
    public string DisplayName => "WEBTOON";
    public string BaseUrl => "https://www.webtoons.com";
    public SourceCapabilities Capabilities => SourceCapabilities.None;

    /// <summary>
    /// Naver's image CDN. Covers and pages are served from <c>webtoon-phinf.pstatic.net</c>, which
    /// hotlink-blocks every Referer but webtoons.com's own — which is the whole reason these URLs go
    /// through the cover proxy rather than straight into an <c>&lt;img&gt;</c> tag.
    /// </summary>
    public IReadOnlyList<string> CoverHosts => ["pstatic.net"];

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    public string? ResolveSeriesIdFromUrl(Uri url)
    {
        // https://www.webtoons.com/en/{genre}/{slug}/list?title_no={n}, and the viewer
        // URL of any episode of it, which carries the same three parts.
        var tail = SourceUrl.PathTail(url, BaseUrl, "/en/");
        var titleNo = QueryValue(url.Query, "title_no");
        if (tail is null || titleNo is null)
        {
            return null;
        }

        var segments = tail.Split('/');
        return segments.Length >= 2 && segments[0].Length > 0 && segments[1].Length > 0
            ? $"{segments[0]}/{segments[1]}/{titleNo}"
            : null;
    }

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var html = await Client.GetStringAsync($"en/search?keyword={Uri.EscapeDataString(title)}", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        // One card markup for both sections of the page (ORIGINALS and CANVAS).
        var results = new List<SourceSeriesResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var card in doc.QuerySelectorAll("a._card_item[href*='title_no=']"))
        {
            var href = card.GetAttribute("href")!;
            if (!Uri.TryCreate(href, UriKind.Absolute, out var url))
            {
                continue;
            }

            var seriesId = ResolveSeriesIdFromUrl(url);
            var name = card.QuerySelector(".info_text .title")?.TextContent.Trim();
            if (seriesId is null || string.IsNullOrEmpty(name) || !seen.Add(seriesId))
            {
                continue;
            }

            var cover = FullSizeImage(card.QuerySelector(".image_wrap img")?.GetAttribute("src"));
            results.Add(new SourceSeriesResult(seriesId, name, href, cover));
        }

        return results;
    }

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var (path, titleNo) = SplitSeriesId(sourceSeriesId);
        var doc = await Parser.ParseDocumentAsync(
            await Client.GetStringAsync(ListUrl(path, titleNo), ct), ct);

        string? Meta(string property) =>
            doc.QuerySelector($"meta[property='{property}']")?.GetAttribute("content")?.Trim();

        var title = Meta("og:title") ?? doc.QuerySelector("h1.subj")?.TextContent.Trim() ?? sourceSeriesId;
        var description = Meta("og:description");

        // The publication line reads "UP EVERY MONDAY" while running and "COMPLETED" once done.
        var schedule = doc.QuerySelector(".day_info")?.TextContent.Trim();
        var status = schedule is null ? null
            : schedule.Contains("COMPLETED", StringComparison.OrdinalIgnoreCase) ? "Completed"
            : "Ongoing";

        return new SourceSeriesDetail(
            sourceSeriesId,
            title,
            Meta("og:url") ?? $"{BaseUrl}/{ListUrl(path, titleNo)}",
            FullSizeImage(Meta("og:image")),
            description,
            status);
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var (path, titleNo) = SplitSeriesId(sourceSeriesId);

        var chapters = new List<SourceChapter>();
        var seen = new HashSet<int>();

        for (var page = 1; page <= MaxListPages; page++)
        {
            var doc = await Parser.ParseDocumentAsync(
                await Client.GetStringAsync($"{ListUrl(path, titleNo)}&page={page}", ct), ct);

            var added = 0;
            foreach (var item in doc.QuerySelectorAll("li._episodeItem"))
            {
                // CANVAS rows repeat data-episode-no on their edit links, so read the
                // attribute off the list item itself rather than any descendant.
                if (!int.TryParse(item.GetAttribute("data-episode-no"), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var episodeNo) ||
                    !seen.Add(episodeNo))
                {
                    continue;
                }

                added++;

                var link = item.QuerySelector("a.detail_list_link")?.GetAttribute("href");
                var episodeTitle = item.QuerySelector(".subj")?.TextContent.Trim();
                var date = item.QuerySelector(".date")?.TextContent.Trim();

                chapters.Add(new SourceChapter(
                    Name,
                    sourceSeriesId,
                    // GetPagesAsync needs the episode's own slug to build its viewer URL.
                    $"{episodeNo}|{EpisodeSlug(link)}",
                    episodeNo.ToString(CultureInfo.InvariantCulture),
                    // Episode numbers, not the "Ep. N" printed in the title: those restart
                    // every season (so they collide on (Number, Language)) and CANVAS has
                    // none at all. episode_no is unique, ascending and permanent — it can
                    // leave gaps where an episode was pulled, which is only cosmetic.
                    episodeNo,
                    Volume: null,
                    Title: string.IsNullOrEmpty(episodeTitle) ? null : episodeTitle,
                    Language: "en",
                    ParseDate(date),
                    Url: link));
            }

            if (added == 0)
            {
                break;
            }
        }

        return SourceChapterList.Normalize(chapters);
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var (path, titleNo) = SplitSeriesId(chapter.SourceSeriesId);
        var (episodeNo, slug) = SplitChapterId(chapter.SourceChapterId);

        var doc = await Parser.ParseDocumentAsync(
            await Client.GetStringAsync(
                $"en/{path}/{slug}/viewer?title_no={titleNo}&episode_no={episodeNo}", ct),
            ct);

        // Scoped to the viewer strip: the page also carries recommendation carousels
        // with their own data-url images.
        var headers = new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" };
        var pages = doc.QuerySelectorAll("#_imageList img[data-url]")
            .Select(img => FullSizeImage(img.GetAttribute("data-url")))
            .Where(url => !string.IsNullOrEmpty(url))
            .Select(url => new PageRequest(url!, headers))
            .ToList();

        return new ChapterPages(pages);
    }

    private static string ListUrl(string path, string titleNo) => $"en/{path}/list?title_no={titleNo}";

    /// <summary>
    /// Splits "{genre}/{slug}/{titleNo}". A bare title number (never minted here, but
    /// possible from a hand-edited mapping) resolves through a placeholder path, which
    /// works for ORIGINALS only — CANVAS titles are not served outside /en/canvas/.
    /// </summary>
    private static (string Path, string TitleNo) SplitSeriesId(string sourceSeriesId)
    {
        var cut = sourceSeriesId.LastIndexOf('/');
        return cut < 0
            ? ("webtoon/series", sourceSeriesId)
            : (sourceSeriesId[..cut], sourceSeriesId[(cut + 1)..]);
    }

    /// <summary>Splits "{episodeNo}|{episodeSlug}"; the slug is decorative and may be absent.</summary>
    private static (string EpisodeNo, string Slug) SplitChapterId(string sourceChapterId)
    {
        var cut = sourceChapterId.IndexOf('|');
        if (cut < 0)
        {
            return (sourceChapterId, "episode");
        }

        var slug = sourceChapterId[(cut + 1)..];
        return (sourceChapterId[..cut], slug.Length == 0 ? "episode" : slug);
    }

    private static string EpisodeSlug(string? viewerUrl)
    {
        // .../en/{genre}/{slug}/{episodeSlug}/viewer?title_no=..&episode_no=..
        if (viewerUrl is null || !Uri.TryCreate(viewerUrl, UriKind.Absolute, out var url))
        {
            return string.Empty;
        }

        var segments = url.AbsolutePath.Trim('/').Split('/');
        var viewer = Array.LastIndexOf(segments, "viewer");
        return viewer > 0 ? segments[viewer - 1] : string.Empty;
    }

    /// <summary>
    /// Strips the "?type=q90" style transform the site appends to every image URL —
    /// it downscales and re-encodes, costing roughly two thirds of the file size.
    /// </summary>
    private static string? FullSizeImage(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        var query = url.IndexOf('?');
        return query < 0 ? url : url[..query];
    }

    private static DateTime? ParseDate(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        DateTime.TryParse(text.Trim(), CultureInfo.GetCultureInfo("en-US"),
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;

    private static string? QueryValue(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=');
            if (split > 0 && pair[..split].Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[(split + 1)..]);
            }
        }

        return null;
    }
}
