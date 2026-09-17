using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Maki.Core.Sources;

namespace Maki.Sources.BaoziManhua;

/// <summary>
/// Baozi Manhua (包子漫画) scraper — Simplified Chinese manhua, <c>cn.baozimh.com</c> (the
/// <c>zh-CN</c>-tagged mirror of the same Nuxt/AMP site; <c>www.baozimh.com</c> is the
/// Traditional-Chinese one). It has no working search: the site is served as static AMP HTML to
/// a plain client, and both <c>/search?q=</c> and <c>/classify?page=N</c> render the same fixed
/// "recommended" list regardless of query or page — the real search/pagination only run after
/// Vue hydrates in a real browser, which a plain HTTP fetch never triggers. So, like TCB Scans,
/// this matches against a cached catalog — except here the catalog really is just that one fixed
/// "recommended" list (~40 titles) off <c>/classify</c>, not the site's full library. Direct
/// series-URL resolution (<see cref="ResolveSeriesIdFromUrl"/>) and everything past search work
/// against the full catalog regardless.
/// <para>
/// A comic id from <c>/classify</c> or a pasted URL may be missing its canonical <c>_xxxxx</c>
/// suffix; <c>/comic/{id}</c> 302s to the suffixed form, which <see cref="Client"/> follows
/// automatically — series/chapter fetches always re-resolve off the redirected URL.
/// </para>
/// <para>
/// A chapter is addressed as "{sectionSlot}_{chapterSlot}" (almost always section 0); fetching
/// <c>/user/page_direct?comic_id=...&amp;section_slot=...&amp;chapter_slot=...</c> 302s to the
/// actual reader page, which currently lives on a different host (<c>twbzmg.com</c>) — followed
/// the same way rather than hardcoded, since that host is the part most likely to rotate.
/// </para>
/// </summary>
public partial class BaoziManhuaSource(IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-baozimanhua";

    private static readonly HtmlParser Parser = new();

    private readonly SourceCatalog _catalog = new(TimeSpan.FromMinutes(30));

    public string Name => "baozimanhua";
    public string DisplayName => "Baozi Manhua";
    public string BaseUrl => "https://cn.baozimh.com";
    public SourceCapabilities Capabilities => SourceCapabilities.None;
    public IReadOnlyList<string> SupportedLanguages => ["zh-Hans"];

    /// <summary>Covers and page images are served off sibling CDN hosts, not this one.</summary>
    public IReadOnlyList<string> CoverHosts => ["baozimh.com", "bzcdn.net"];

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    [GeneratedRegex(@"section_slot=(\d+).*chapter_slot=(\d+)")]
    private static partial Regex SlotRegex();

    public string? ResolveSeriesIdFromUrl(Uri url) =>
        SourceUrl.PathTail(url, BaseUrl, "/comic/", firstSegmentOnly: true);

    // ── Search (catalog fallback — see class remarks) ───────────────────

    public Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default) =>
        _catalog.SearchAsync(title, FetchCatalogAsync, ct);

    private async Task<List<SourceSeriesResult>> FetchCatalogAsync(CancellationToken ct)
    {
        var doc = await GetHtmlAsync("classify", ct);
        var catalog = new List<SourceSeriesResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var link in doc.QuerySelectorAll("div.comics-card a.comics-card__poster"))
        {
            var href = link.GetAttribute("href");
            var seriesId = href is null ? null : SourceUrl.PathTail(new Uri(BaseUrl + href), BaseUrl, "/comic/", firstSegmentOnly: true);
            var title = link.GetAttribute("title");
            if (seriesId is null || string.IsNullOrEmpty(title) || !seen.Add(seriesId))
            {
                continue;
            }

            var cover = link.QuerySelector("amp-img")?.GetAttribute("src");
            catalog.Add(new SourceSeriesResult(seriesId, title, $"{BaseUrl}/comic/{seriesId}", cover));
        }

        return catalog;
    }

    // ── Series detail ─────────────────────────────────────────────────

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var (doc, canonicalId) = await GetSeriesDocAsync(sourceSeriesId, ct);

        var title = doc.QuerySelector("h1.comics-detail__title")?.TextContent.Trim() ?? canonicalId;
        var description = doc.QuerySelector("p.comics-detail__desc")?.TextContent.Trim();
        var cover = doc.QuerySelector("meta[name='og:image']")?.GetAttribute("content");
        var statusText = doc.QuerySelector("meta[name='og:novel:status']")?.GetAttribute("content");
        var status = statusText switch
        {
            "连载中" => "Ongoing",
            "已完结" => "Completed",
            _ => statusText
        };

        return new SourceSeriesDetail(canonicalId, title, $"{BaseUrl}/comic/{canonicalId}", cover, description, status);
    }

    // ── Chapters ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var (doc, canonicalId) = await GetSeriesDocAsync(sourceSeriesId, ct);

        var chapters = new List<SourceChapter>();
        foreach (var link in doc.QuerySelectorAll("a.comics-chapters__item"))
        {
            var href = link.GetAttribute("href");
            var match = href is null ? null : SlotRegex().Match(href);
            if (match is not { Success: true })
            {
                continue;
            }

            var section = match.Groups[1].Value;
            var slot = match.Groups[2].Value;
            var label = link.TextContent.Trim();

            // Chapter labels are Chinese ("第1186话 ..."), which ChapterNumberParser's English
            // "ch"/"chapter" patterns don't recognize — chapter_slot is the site's own chapter
            // number and is authoritative anyway.
            decimal? number = decimal.TryParse(slot, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n
                : null;

            chapters.Add(new SourceChapter(
                Name,
                canonicalId,
                $"{section}_{slot}",
                label,
                number,
                Volume: null,
                Title: null,
                Language: "zh-Hans",
                ReleaseDate: null,
                Url: $"{BaseUrl}/user/page_direct?comic_id={canonicalId}&section_slot={section}&chapter_slot={slot}"));
        }

        return SourceChapterList.Normalize(chapters);
    }

    // ── Page images ───────────────────────────────────────────────────

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var parts = chapter.SourceChapterId.Split('_', 2);
        if (parts.Length != 2)
        {
            return new ChapterPages([]);
        }

        var html = await Client.GetStringAsync(
            $"user/page_direct?comic_id={chapter.SourceSeriesId}&section_slot={parts[0]}&chapter_slot={parts[1]}", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var pages = doc.QuerySelectorAll("amp-img[id^='chapter-img-']")
            .Select(img => img.GetAttribute("src"))
            .Where(src => !string.IsNullOrEmpty(src))
            .Select(src => new PageRequest(src!))
            .ToList();

        return new ChapterPages(pages);
    }

    /// <summary>
    /// Fetches <c>/comic/{id}</c>, following the 302 a non-canonical id gets, and returns both
    /// the parsed document and the canonical id read back off the final URL.
    /// </summary>
    private async Task<(AngleSharp.Html.Dom.IHtmlDocument Doc, string CanonicalId)> GetSeriesDocAsync(
        string sourceSeriesId, CancellationToken ct)
    {
        using var response = await Client.GetAsync($"comic/{sourceSeriesId}", ct);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var finalUrl = response.RequestMessage?.RequestUri;
        var canonicalId = finalUrl is not null
            ? SourceUrl.PathTail(finalUrl, BaseUrl, "/comic/", firstSegmentOnly: true) ?? sourceSeriesId
            : sourceSeriesId;

        return (doc, canonicalId);
    }

    private async Task<AngleSharp.Html.Dom.IHtmlDocument> GetHtmlAsync(string path, CancellationToken ct)
    {
        var html = await Client.GetStringAsync(path, ct);
        return await Parser.ParseDocumentAsync(html, ct);
    }
}
