using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Maki.Core.Http;
using Maki.Core.Parsing;
using Maki.Core.Sources;

namespace Maki.Sources.MangaWorld;

/// <summary>
/// MangaWorld scraper (Italian scanlations aggregator). Passive Cloudflare today (a plain client
/// gets 200, no cf-mitigated), so it doesn't declare NeedsFlareSolverr, but it is still built on
/// <see cref="IHtmlFetcher"/> so a future challenge falls back to FlareSolverr. Series id is
/// "{numericId}/{slug}" so URLs rebuild without a lookup; chapter id is the 24-hex id after "/read/".
/// </summary>
public partial class MangaWorldSource(IHtmlFetcher fetcher) : ISource
{
    private static readonly HtmlParser Parser = new();
    private static readonly CultureInfo ItalianCulture = CultureInfo.GetCultureInfo("it-IT");

    public string Name => "mangaworld";
    public string DisplayName => "MangaWorld";

    public string BaseUrl =>
        Environment.GetEnvironmentVariable("MAKI_SOURCE_MANGAWORLD_BASEURL")?.TrimEnd('/') ?? "https://www.mangaworld.mx";

    public SourceCapabilities Capabilities => SourceCapabilities.None;
    public IReadOnlyList<string> SupportedLanguages => ["it"];

    [GeneratedRegex(@"capitolo\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex CapitoloPattern();

    public string? ResolveSeriesIdFromUrl(Uri url)
    {
        var tail = SourceUrl.PathTail(url, BaseUrl, "/manga/");
        return tail is not null && tail.Split('/').Length == 2 ? tail : null;
    }

    // ── Search ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var html = await fetcher.GetHtmlAsync($"{BaseUrl}/archive?keyword={Uri.EscapeDataString(title)}", ct);
        EnsureNotCookieShell(html);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var results = new List<SourceSeriesResult>();
        foreach (var entry in doc.QuerySelectorAll("div.comics-grid div.entry"))
        {
            var link = entry.QuerySelector("a.thumb");
            var href = link?.GetAttribute("href");
            if (link is null || string.IsNullOrEmpty(href))
            {
                continue;
            }

            var seriesId = SeriesIdFromHref(href);
            if (seriesId is null)
            {
                continue;
            }

            var titleText = link.GetAttribute("title") ?? entry.QuerySelector("p.name a.manga-title")?.TextContent;
            if (string.IsNullOrWhiteSpace(titleText))
            {
                continue;
            }

            var cover = entry.QuerySelector("a.thumb img")?.GetAttribute("src");
            var description = entry.QuerySelector("div.story")?.TextContent.Trim();

            results.Add(new SourceSeriesResult(
                seriesId, titleText.Trim(), $"{BaseUrl}/manga/{seriesId}", cover,
                string.IsNullOrEmpty(description) ? null : description));
        }

        return results;
    }

    // ── Series detail ─────────────────────────────────────────────────

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var html = await fetcher.GetHtmlAsync($"{BaseUrl}/manga/{sourceSeriesId}", ct);
        EnsureNotCookieShell(html);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var info = doc.QuerySelector("div.comic-info");
        var title = info?.QuerySelector("h1.name")?.TextContent.Trim();
        var cover = info?.QuerySelector(".thumb > img")?.GetAttribute("src");

        var statusText = info?.QuerySelector("a[href*='/archive?status=']")?.TextContent.Trim();
        var status = MapStatus(statusText);

        var description = doc.QuerySelector("#noidungm")?.TextContent.Trim();

        return new SourceSeriesDetail(
            sourceSeriesId,
            string.IsNullOrEmpty(title) ? sourceSeriesId : title,
            $"{BaseUrl}/manga/{sourceSeriesId}",
            cover,
            string.IsNullOrEmpty(description) ? null : description,
            status);
    }

    private static string? MapStatus(string? statusText) => statusText switch
    {
        "In corso" => "Ongoing",
        "Finito" => "Completed",
        _ => null
    };

    // ── Chapters ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var html = await fetcher.GetHtmlAsync($"{BaseUrl}/manga/{sourceSeriesId}", ct);
        EnsureNotCookieShell(html);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var chapters = new List<SourceChapter>();
        foreach (var chapterDiv in doc.QuerySelectorAll(".chapters-wrapper .chapter"))
        {
            var link = chapterDiv.QuerySelector("a.chap");
            var href = link?.GetAttribute("href");
            var chapterId = href is null ? null : ChapterIdFromHref(href);
            if (chapterId is null)
            {
                continue;
            }

            var numberRaw = chapterDiv.QuerySelector("span.d-inline-block")?.TextContent.Trim();
            var volumeRaw = EnclosingVolumeName(chapterDiv);

            var capitolo = CapitoloPattern().Match(numberRaw ?? string.Empty);
            var parsed = capitolo.Success
                ? ChapterNumberParser.Parse(capitolo.Groups[1].Value, volumeRaw)
                : ChapterNumberParser.Parse(numberRaw, volumeRaw);

            var dateText = chapterDiv.QuerySelectorAll("i.chap-date").LastOrDefault()?.TextContent.Trim();
            var releaseDate = ParseItalianDate(dateText);

            chapters.Add(new SourceChapter(
                Name,
                sourceSeriesId,
                chapterId,
                numberRaw,
                parsed.Number,
                parsed.Volume,
                // Null-number chapters need a non-null title so ChapterIdentity can dedupe by it.
                Title: parsed.Number is null ? FallbackTitle(numberRaw, link, volumeRaw, chapterId) : null,
                Language: "it",
                releaseDate,
                Url: href));
        }

        // Site lists newest first; normalize to ascending and drop duplicates.
        return SourceChapterList.Normalize(chapters);
    }

    /// <summary>
    /// Title for a null-number chapter, always the site's own text: the span label ("Oneshot") when
    /// there is one, else the chapter anchor's text with the date stripped out, else the enclosing
    /// volume name, else the chapter id as a last resort for a chapter div bare of any text at all.
    /// </summary>
    private static string FallbackTitle(string? numberRaw, IElement? link, string? volumeRaw, string chapterId)
    {
        if (!string.IsNullOrEmpty(numberRaw))
        {
            return numberRaw;
        }

        var anchorText = AnchorTextWithoutDate(link);
        if (!string.IsNullOrEmpty(anchorText))
        {
            return anchorText;
        }

        return !string.IsNullOrEmpty(volumeRaw) ? volumeRaw : chapterId;
    }

    /// <summary>The chapter anchor's text with any "i.chap-date" descendant removed first.</summary>
    private static string? AnchorTextWithoutDate(IElement? link)
    {
        if (link is null)
        {
            return null;
        }

        var clone = (IElement)link.Clone(deep: true);
        foreach (var date in clone.QuerySelectorAll("i.chap-date").ToList())
        {
            date.Remove();
        }

        return clone.TextContent.Trim();
    }

    /// <summary>
    /// Walks up from a chapter div to its enclosing ".volume-element" and reads "p.volume-name".
    /// Flat (no-volume) titles have no such ancestor and return null before the walk escapes
    /// ".chapters-wrapper" itself.
    /// </summary>
    private static string? EnclosingVolumeName(IElement chapterDiv)
    {
        var node = chapterDiv.ParentElement;
        while (node is not null)
        {
            if (node.ClassList.Contains("volume-element"))
            {
                return node.QuerySelector("p.volume-name")?.TextContent.Trim();
            }

            if (node.ClassList.Contains("chapters-wrapper"))
            {
                return null;
            }

            node = node.ParentElement;
        }

        return null;
    }

    /// <summary>Italian long-form date, e.g. "25 Settembre 2026". Unparseable returns null.</summary>
    private static DateTime? ParseItalianDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateTime.TryParseExact(
            text, "dd MMMM yyyy", ItalianCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    // ── Page images ───────────────────────────────────────────────────

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        // The default paged reader renders zero images; only the list style renders all of them.
        var url = $"{BaseUrl}/manga/{chapter.SourceSeriesId}/read/{chapter.SourceChapterId}?style=list";
        var html = await fetcher.GetHtmlAsync(url, ct);
        EnsureNotCookieShell(html);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var headers = new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" };
        var pages = doc.QuerySelectorAll("div#page img.page-image")
            .Select(img => img.GetAttribute("src"))
            .Where(src => !string.IsNullOrEmpty(src))
            .Select(src => new PageRequest(src!, headers))
            .ToList();

        if (pages.Count == 0)
        {
            throw new ChapterLockedException($"MangaWorld chapter {chapter.SourceChapterId} has no pages");
        }

        return new ChapterPages(pages);
    }

    /// <summary>Pulls "{id}/{slug}" out of a "/manga/{id}/{slug}" href, absolute or relative.</summary>
    private static string? SeriesIdFromHref(string href)
    {
        var path = Uri.TryCreate(href, UriKind.Absolute, out var uri) ? uri.AbsolutePath : href;
        const string marker = "/manga/";

        var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var segments = path[(index + marker.Length)..].Trim('/').Split('/');
        return segments.Length >= 2 ? $"{segments[0]}/{segments[1]}" : null;
    }

    /// <summary>Pulls the 24-hex chapter id out of a "/read/{id}" href, dropping any query string.</summary>
    private static string? ChapterIdFromHref(string href)
    {
        const string marker = "/read/";
        var index = href.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var tail = href[(index + marker.Length)..].Trim('/');
        var id = tail.Split('/', '?')[0];
        return id.Length == 0 ? null : id;
    }

    /// <summary>
    /// A small page setting "document.cookie=MWCookie=..." and reloading, not served today but not
    /// recognised by ChallengeAwareFetcher's challenge detection either. Left unhandled it would read
    /// back as a normal 200 page with no chapters, silently stalling a monitored series, so a sync
    /// fails loudly instead.
    /// </summary>
    private static void EnsureNotCookieShell(string html)
    {
        if (html.Contains("MWCookie", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "MangaWorld served an MWCookie interstitial instead of the page");
        }
    }
}
