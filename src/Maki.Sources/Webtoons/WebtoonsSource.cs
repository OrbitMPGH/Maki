using System.Globalization;
using System.Net;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Maki.Core.Http;
using Maki.Core.Sources;

namespace Maki.Sources.Webtoons;

/// <summary>
/// WEBTOON (webtoons.com) scraper — the official Naver/LINE platform, covering both
/// ORIGINALS and reader-published CANVAS titles across seven locale services
/// (en, id, th, es, fr, zh-hant, de).
/// <para>
/// Plain server-rendered HTML, no Cloudflare. English series id is
/// "{genre}/{slug}/{titleNo}" (unchanged, for back-compat with every id stored before
/// locales existed); every other locale prefixes it, "{locale}/{genre}/{slug}/{titleNo}",
/// because each locale runs its own service with its own <c>title_no</c>, the same
/// situation as MANGA Plus, not a language filter on one series. Chapter id is
/// "{episodeNo}|{episodeSlug}": only the numeric ids actually select anything (a wrong
/// genre/slug redirects to the canonical URL), but carrying the path keeps the links we
/// hand the UI real and saves a redirect hop per fetch. The one exception is CANVAS,
/// whose titles 404 outside the literal <c>canvas</c> segment, which is why the path is
/// stored rather than rebuilt from a placeholder.
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
    /// <para>
    /// Only reached as a fallback: the mobile API below returns the whole list in one
    /// request, and the HTML walk only runs when that API errors or a title predates it.
    /// </para>
    /// </summary>
    private const int MaxListPages = 500;

    /// <summary>Locale path segment -&gt; the language tag stored on <see cref="Chapter"/>.</summary>
    private static readonly IReadOnlyDictionary<string, string> LocaleLanguages =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["en"] = "en",
            ["id"] = "id",
            ["th"] = "th",
            ["es"] = "es",
            ["fr"] = "fr",
            ["zh-hant"] = "zh-Hant",
            ["de"] = "de",
        };

    /// <summary>
    /// Search fan-out order. English first matters: <c>ScrobbleMatching.BestCandidate</c>
    /// keeps the first of equal scores, so an auto-match that finds the same title in
    /// several locales still links the English edition.
    /// </summary>
    private static readonly string[] SearchLocales = ["en", "id", "th", "es", "fr", "zh-hant", "de"];

    private static readonly JsonSerializerOptions ApiJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly HtmlParser Parser = new();

    public string Name => "webtoons";
    public string DisplayName => "WEBTOON";
    public string BaseUrl => "https://www.webtoons.com";
    public SourceCapabilities Capabilities => SourceCapabilities.None;
    public SourceKind Kind => SourceKind.Official;
    public SourceContent Content => SourceContent.Manhwa | SourceContent.Webtoon;

    public IReadOnlyList<string> SupportedLanguages => ["en", "id", "th", "es", "fr", "zh-Hant", "de"];

    /// <summary>
    /// Naver's image CDN. Covers and pages are served from <c>webtoon-phinf.pstatic.net</c>, which
    /// hotlink-blocks every Referer but webtoons.com's own — which is the whole reason these URLs go
    /// through the cover proxy rather than straight into an <c>&lt;img&gt;</c> tag.
    /// </summary>
    public IReadOnlyList<string> CoverHosts => ["pstatic.net"];

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    public string? ResolveSeriesIdFromUrl(Uri url)
    {
        // https://www.webtoons.com/{locale}/{genre}/{slug}/list?title_no={n}, and the
        // viewer URL of any episode of it, which carries the same parts.
        var segments = url.AbsolutePath.Trim('/').Split('/');
        if (segments.Length < 3 || !LocaleLanguages.ContainsKey(segments[0]))
        {
            return null;
        }

        var locale = segments[0];
        var tail = SourceUrl.PathTail(url, BaseUrl, $"/{locale}/");
        var titleNo = QueryValue(url.Query, "title_no");
        if (tail is null || titleNo is null)
        {
            return null;
        }

        var tailSegments = tail.Split('/');
        if (tailSegments.Length < 2 || tailSegments[0].Length == 0 || tailSegments[1].Length == 0)
        {
            return null;
        }

        // English never gets a locale prefix, so a new English mapping equals a stored one.
        return locale == "en"
            ? $"{tailSegments[0]}/{tailSegments[1]}/{titleNo}"
            : $"{locale}/{tailSegments[0]}/{tailSegments[1]}/{titleNo}";
    }

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var perLocale = await Task.WhenAll(SearchLocales.Select(locale => SearchLocaleAsync(locale, title, ct)));

        var results = new List<SourceSeriesResult>();
        foreach (var localeResults in perLocale)
        {
            results.AddRange(localeResults);
        }

        return results;
    }

    /// <summary>
    /// One locale's search page. Failures here (a locale that is briefly down, one this
    /// account's region can't reach, or one the shared limiter rate-limits) only drop that
    /// locale's hits, they don't fail the whole seven-locale search.
    /// </summary>
    private async Task<IReadOnlyList<SourceSeriesResult>> SearchLocaleAsync(
        string locale, string title, CancellationToken ct)
    {
        string html;
        try
        {
            html = await Client.GetStringAsync($"{locale}/search?keyword={Uri.EscapeDataString(title)}", ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or RateLimitException)
        {
            return [];
        }

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
        var (locale, path, titleNo) = SplitSeriesId(sourceSeriesId);
        var doc = await Parser.ParseDocumentAsync(
            await Client.GetStringAsync(ListUrl(locale, path, titleNo), ct), ct);

        string? Meta(string property) =>
            doc.QuerySelector($"meta[property='{property}']")?.GetAttribute("content")?.Trim();

        var title = Meta("og:title") ?? doc.QuerySelector("h1.subj")?.TextContent.Trim() ?? sourceSeriesId;
        var description = Meta("og:description");

        // Every locale renders the same two icon classes on the schedule line; the text
        // beside them is localized ("UP EVERY MONDAY", "TODOS LOS LUNES", "更新"...), so it
        // can't be matched on English words.
        var status = doc.QuerySelector(".day_info span.txt_ico_completed") is not null ? "Completed"
            : doc.QuerySelector(".day_info span.txt_ico_up") is not null ? "Ongoing"
            : null;

        return new SourceSeriesDetail(
            sourceSeriesId,
            title,
            Meta("og:url") ?? $"{BaseUrl}/{ListUrl(locale, path, titleNo)}",
            FullSizeImage(Meta("og:image")),
            description,
            status);
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var (locale, path, titleNo) = SplitSeriesId(sourceSeriesId);
        var language = LocaleLanguages[locale];

        return await ListChaptersFromApiAsync(sourceSeriesId, locale, path, titleNo, language, ct)
            ?? await ListChaptersFromHtmlAsync(sourceSeriesId, locale, path, titleNo, language, ct);
    }

    /// <summary>
    /// The mobile API returns the whole episode list in one request, replacing the
    /// 10-per-page HTML walk (Tower of God is 652 episodes = 73 pages). Returns null on
    /// anything short of a clean 200 with <c>success:true</c> and a non-null result, which
    /// falls back to the HTML walk below rather than reporting an empty series.
    /// </summary>
    private async Task<List<SourceChapter>?> ListChaptersFromApiAsync(
        string sourceSeriesId, string locale, string path, string titleNo, string language, CancellationToken ct)
    {
        var type = path.Split('/')[0] == "canvas" ? "canvas" : "webtoon";
        var url = $"https://m.webtoons.com/api/v1/{type}/{titleNo}/episodes?pageSize=99999";
        if (type == "canvas")
        {
            url += $"&readingLanguageCode={locale}";
        }

        EpisodeListResponse? response = null;

        // m.webtoons.com throws intermittent SSL errors; one retry before giving up on the
        // API entirely and falling back to the HTML walk. RateLimitException is deliberately
        // not caught here: falling back would turn one rate-limited request into up to 73
        // HTML requests against the same limiter, making the rate limit worse. Let it
        // propagate so the sync fails outright and the shared queue cooldown applies.
        for (var attempt = 0; attempt < 2 && response is null; attempt++)
        {
            try
            {
                var json = await Client.GetStringAsync(url, ct);
                response = JsonSerializer.Deserialize<EpisodeListResponse>(json, ApiJsonOptions);
            }
            catch (HttpRequestException)
            {
            }
        }

        if (response is not { Success: true, Result.EpisodeList: { } episodes })
        {
            return null;
        }

        var chapters = new List<SourceChapter>(episodes.Count);
        foreach (var episode in episodes)
        {
            var absoluteViewerUrl = episode.ViewerLink is null ? null : BaseUrl + episode.ViewerLink;
            chapters.Add(new SourceChapter(
                Name,
                sourceSeriesId,
                $"{episode.EpisodeNo}|{EpisodeSlug(absoluteViewerUrl)}",
                episode.EpisodeNo.ToString(CultureInfo.InvariantCulture),
                episode.EpisodeNo,
                Volume: null,
                Title: string.IsNullOrEmpty(episode.EpisodeTitle) ? null : WebUtility.HtmlDecode(episode.EpisodeTitle),
                Language: language,
                DateTimeOffset.FromUnixTimeMilliseconds(episode.ExposureDateMillis).UtcDateTime,
                Url: absoluteViewerUrl));
        }

        return SourceChapterList.Normalize(chapters);
    }

    private async Task<List<SourceChapter>> ListChaptersFromHtmlAsync(
        string sourceSeriesId, string locale, string path, string titleNo, string language, CancellationToken ct)
    {
        var chapters = new List<SourceChapter>();
        var seen = new HashSet<int>();

        for (var page = 1; page <= MaxListPages; page++)
        {
            var doc = await Parser.ParseDocumentAsync(
                await Client.GetStringAsync($"{ListUrl(locale, path, titleNo)}&page={page}", ct), ct);

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
                    Language: language,
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
        var (locale, path, titleNo) = SplitSeriesId(chapter.SourceSeriesId);
        var (episodeNo, slug) = SplitChapterId(chapter.SourceChapterId);

        var doc = await Parser.ParseDocumentAsync(
            await Client.GetStringAsync(
                $"{locale}/{path}/{slug}/viewer?title_no={titleNo}&episode_no={episodeNo}", ct),
            ct);

        // Scoped to the viewer strip: the page also carries recommendation carousels
        // with their own data-url images.
        var headers = new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" };
        var pages = doc.QuerySelectorAll("#_imageList img[data-url]")
            .Select(img => FullSizeImage(img.GetAttribute("data-url")))
            .Where(url => !string.IsNullOrEmpty(url))
            .Select(url => new PageRequest(url!, headers))
            .ToList();

        if (pages.Count == 0)
        {
            // The API only lists published episodes, so an empty viewer strip on one of
            // those means the chapter is behind a Fast Pass window rather than missing.
            throw new ChapterLockedException($"No pages found for episode {episodeNo}");
        }

        return new ChapterPages(pages);
    }

    private static string ListUrl(string locale, string path, string titleNo) => $"{locale}/{path}/list?title_no={titleNo}";

    /// <summary>
    /// Splits a series id into its locale, "{genre}/{slug}" path and title number.
    /// English ids carry no locale prefix (back-compat with every id stored before
    /// locales existed): "fantasy/tower-of-god/95" is 3 segments and resolves as "en".
    /// A non-English locale adds a fourth segment in front: "es/fantasy/tower-of-god/1718".
    /// A bare title number (never minted here, but possible from a hand-edited mapping)
    /// resolves through a placeholder path as English, which works for ORIGINALS only:
    /// CANVAS titles are not served outside /en/canvas/.
    /// </summary>
    private static (string Locale, string Path, string TitleNo) SplitSeriesId(string sourceSeriesId)
    {
        var segments = sourceSeriesId.Split('/');
        if (segments.Length >= 4 && segments[0] != "en" && LocaleLanguages.ContainsKey(segments[0]))
        {
            var (path, titleNo) = SplitPathAndTitleNo(string.Join('/', segments[1..]));
            return (segments[0], path, titleNo);
        }

        var (enPath, enTitleNo) = SplitPathAndTitleNo(sourceSeriesId);
        return ("en", enPath, enTitleNo);
    }

    private static (string Path, string TitleNo) SplitPathAndTitleNo(string id)
    {
        var cut = id.LastIndexOf('/');
        return cut < 0
            ? ("webtoon/series", id)
            : (id[..cut], id[(cut + 1)..]);
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
        // .../{locale}/{genre}/{slug}/{episodeSlug}/viewer?title_no=..&episode_no=..
        // zh-hant slugs arrive percent-encoded from the API; Uri keeps them that way, which
        // is what we want, since the same escaped form has to round-trip back into the URL.
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

    /// <summary>Shape of <c>GET m.webtoons.com/api/v1/{type}/{titleNo}/episodes</c>.</summary>
    private sealed record EpisodeListResponse(EpisodeListResult? Result, bool Success);

    private sealed record EpisodeListResult(List<ApiEpisode>? EpisodeList);

    private sealed record ApiEpisode(
        int EpisodeNo,
        string? EpisodeTitle,
        string? ViewerLink,
        long ExposureDateMillis);
}
