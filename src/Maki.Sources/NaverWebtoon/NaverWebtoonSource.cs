using System.Globalization;
using System.Net;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Maki.Core.Sources;

namespace Maki.Sources.NaverWebtoon;

/// <summary>
/// Naver Webtoon (comic.naver.com), the official Korean platform. Covers only the
/// WEBTOON tier (paid serialisation); Best Challenge and Challenge (amateur tiers)
/// are out of scope. Plain JSON API for search/detail/chapters, HTML for pages.
/// Series id is the numeric titleId; chapter id is the episode "no", both as strings.
/// An "adult" title 401s its chapter list and every episode redirects to a Naver
/// login wall, so those are skipped at search time and defended again at list/page time.
/// </summary>
public class NaverWebtoonSource(IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-naverwebtoon";

    /// <summary>Chapter list pages 20 at a time; a runaway guard, not a real ceiling.</summary>
    private const int MaxListPages = 500;

    private static readonly TimeSpan KstOffset = TimeSpan.FromHours(9);
    private static readonly HtmlParser Parser = new();

    public string Name => "naverwebtoon";
    public string DisplayName => "Naver Webtoon";
    public string BaseUrl => "https://comic.naver.com";
    public SourceCapabilities Capabilities => SourceCapabilities.None;
    public SourceKind Kind => SourceKind.Official;
    public SourceContent Content => SourceContent.Manhwa | SourceContent.Webtoon;
    public IReadOnlyList<string> SupportedLanguages => ["ko"];

    /// <summary>Covers and pages are served from image-comic.pstatic.net.</summary>
    public IReadOnlyList<string> CoverHosts => ["pstatic.net"];

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    public string? ResolveSeriesIdFromUrl(Uri url)
    {
        // https://comic.naver.com/webtoon/list?titleId=769209 (and the m. mobile host).
        // PathTail's www.-tolerant host check doesn't fit the m. host, so it's checked by hand.
        if (!url.Host.Equals("comic.naver.com", StringComparison.OrdinalIgnoreCase) &&
            !url.Host.Equals("m.comic.naver.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!url.AbsolutePath.Equals("/webtoon/list", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var titleId = QueryValue(url.Query, "titleId");
        return !string.IsNullOrEmpty(titleId) && titleId.All(char.IsAsciiDigit) ? titleId : null;
    }

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var root = await GetAsync($"api/search/webtoon?keyword={Uri.EscapeDataString(title)}&page=1", ct);
        if (!root.TryGetProperty("searchList", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<SourceSeriesResult>();
        foreach (var item in list.EnumerateArray())
        {
            // Adult hits 401 on the chapter list and redirect every episode to a login wall,
            // so they are dropped here rather than surfaced as an unusable search result.
            if (item.TryGetProperty("adult", out var adultEl) && adultEl.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            var id = TitleId(item);
            if (id is null)
            {
                continue;
            }

            var name = item.TryGetProperty("titleName", out var n) ? n.GetString() : null;
            var cover = item.TryGetProperty("thumbnailUrl", out var c) ? c.GetString() : null;
            var synopsis = item.TryGetProperty("synopsis", out var s) ? s.GetString() : null;

            results.Add(new SourceSeriesResult(
                id, string.IsNullOrEmpty(name) ? id : name, $"{BaseUrl}/webtoon/list?titleId={id}", cover, synopsis));
        }

        return results;
    }

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var root = await GetAsync($"api/article/list/info?titleId={sourceSeriesId}", ct);

        var title = root.TryGetProperty("titleName", out var t) ? t.GetString() ?? sourceSeriesId : sourceSeriesId;
        var cover = root.TryGetProperty("thumbnailUrl", out var c) ? c.GetString() : null;
        var description = root.TryGetProperty("synopsis", out var d) ? d.GetString() : null;

        var finished = root.TryGetProperty("finished", out var f) && f.ValueKind == JsonValueKind.True;
        var rest = root.TryGetProperty("rest", out var r) && r.ValueKind == JsonValueKind.True;
        var status = rest ? "Hiatus" : finished ? "Completed" : "Ongoing";

        return new SourceSeriesDetail(
            sourceSeriesId, title, $"{BaseUrl}/webtoon/list?titleId={sourceSeriesId}", cover, description, status);
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var chapters = new List<SourceChapter>();
        var page = 1;

        for (var i = 0; i < MaxListPages && page != 0; i++)
        {
            JsonElement root;
            try
            {
                root = await GetAsync($"api/article/list?titleId={sourceSeriesId}&page={page}", ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                // A 401 here means an adult title: never return an empty list for one, which
                // would read as "no chapters" rather than "this title cannot be read here".
                throw new InvalidOperationException(
                    $"Naver Webtoon title {sourceSeriesId} is adult-restricted and requires a login");
            }

            if (root.TryGetProperty("articleList", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    var chapter = ParseChapter(sourceSeriesId, item);
                    if (chapter is not null)
                    {
                        chapters.Add(chapter);
                    }
                }
            }

            page = root.TryGetProperty("pageInfo", out var pageInfo) &&
                   pageInfo.TryGetProperty("nextPage", out var nextPageEl) &&
                   nextPageEl.ValueKind == JsonValueKind.Number
                ? nextPageEl.GetInt32()
                : 0;
        }

        return SourceChapterList.Normalize(chapters);
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var response = await Client.GetAsync(
            $"webtoon/detail?titleId={chapter.SourceSeriesId}&no={chapter.SourceChapterId}", ct);
        response.EnsureSuccessStatusCode();

        var finalUri = response.RequestMessage?.RequestUri;

        // A login wall never unlocks; an adult title lands here even though it was already
        // dropped from search, in case a mapping was created by hand-pasted URL.
        if (finalUri is not null && finalUri.Host.Equals("nid.naver.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Naver Webtoon chapter {chapter.SourceChapterId} of {chapter.SourceSeriesId} redirected to a login wall");
        }

        // A paid chapter, or one whose free window closed after it was listed, redirects to
        // the series page instead of serving the viewer.
        if (finalUri is not null && finalUri.AbsolutePath.Equals("/webtoon/list", StringComparison.OrdinalIgnoreCase))
        {
            throw new ChapterLockedException(
                $"Naver Webtoon chapter {chapter.SourceChapterId} of {chapter.SourceSeriesId} is not free");
        }

        var html = await response.Content.ReadAsStringAsync(ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var headers = new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" };
        var pages = doc.QuerySelectorAll(".wt_viewer img[id^='content_image_']")
            .Select(img => img.GetAttribute("src"))
            .Where(src => !string.IsNullOrEmpty(src))
            .Select(src => new PageRequest(src!, headers))
            .ToList();

        if (pages.Count == 0)
        {
            throw new ChapterLockedException(
                $"Naver Webtoon chapter {chapter.SourceChapterId} of {chapter.SourceSeriesId} served no pages");
        }

        return new ChapterPages(pages);
    }

    private SourceChapter? ParseChapter(string sourceSeriesId, JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("no", out var noEl) ||
            noEl.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        // Naver marks a paid or not-yet-free episode with charge/thumbnailLock; these unlock
        // only by purchase (or by waiting inside the app, which the web API never reflects),
        // so they are dropped here rather than listed and left for GetPagesAsync to fail on.
        var charge = item.TryGetProperty("charge", out var chargeEl) && chargeEl.ValueKind == JsonValueKind.True;
        var thumbnailLock = item.TryGetProperty("thumbnailLock", out var lockEl) && lockEl.ValueKind == JsonValueKind.True;
        if (charge || thumbnailLock)
        {
            return null;
        }

        var no = noEl.GetInt32();
        var subtitle = item.TryGetProperty("subtitle", out var subtitleEl) ? subtitleEl.GetString() : null;
        var dateText = item.TryGetProperty("serviceDateDescription", out var dateEl) ? dateEl.GetString() : null;
        var numberRaw = no.ToString(CultureInfo.InvariantCulture);

        return new SourceChapter(
            Name,
            sourceSeriesId,
            numberRaw,
            numberRaw,
            no,
            Volume: null,
            Title: string.IsNullOrWhiteSpace(subtitle) ? null : subtitle,
            Language: "ko",
            ParseServiceDate(dateText),
            Url: $"{BaseUrl}/webtoon/detail?titleId={sourceSeriesId}&no={no}");
    }

    /// <summary>
    /// "26.09.22" is yy.MM.dd in KST (UTC+9, no DST). Today's own episodes print a bare time
    /// ("10:23") instead, so those fall back to today's date in KST. Anything else is unparseable.
    /// </summary>
    private static DateTime? ParseServiceDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();

        if (trimmed.Contains(':'))
        {
            var nowKst = DateTimeOffset.UtcNow.ToOffset(KstOffset);
            return new DateTimeOffset(nowKst.Year, nowKst.Month, nowKst.Day, 0, 0, 0, KstOffset).UtcDateTime;
        }

        if (!DateTime.TryParseExact(
                trimmed, "yy.MM.dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return null;
        }

        return new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, KstOffset).UtcDateTime;
    }

    private static string? TitleId(JsonElement item)
    {
        if (!item.TryGetProperty("titleId", out var idEl))
        {
            return null;
        }

        return idEl.ValueKind switch
        {
            JsonValueKind.Number => idEl.GetInt64().ToString(CultureInfo.InvariantCulture),
            JsonValueKind.String => idEl.GetString(),
            _ => null
        };
    }

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

    private async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        var body = await Client.GetStringAsync(path, ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }
}
