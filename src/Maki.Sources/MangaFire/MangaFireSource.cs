using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Maki.Core.Sources;

namespace Maki.Sources.MangaFire;

/// <summary>
/// MangaFire scraper. The site is a React SPA over a JSON API whose protected endpoints
/// (<c>/api/titles…</c>, <c>/api/chapters/…</c>) now require a client-signed <c>vrf</c> token minted
/// by an obfuscated anti-tamper module — it can't be reproduced server-side, so every call is driven
/// through a real headless browser (<see cref="MangaFireBrowser"/>) that lets the site sign and issue
/// the request, and we read the JSON response back off the network. Endpoints reached this way:
///   /browse?keyword={q}          → search:   {items:[{url, title, poster}]}
///   /title/{hid}[-{slug}]        → detail:   {data:{title, synopsisHtml, status, …}}
///   (title page, walking pager)  → chapters: {items:[{id, number, name, language, type, createdAt}]}
///   /title/{key}/chapter/{id}    → pages:    {data:{pages:[{url, width, height}]}}
/// Series id is "{hid}-{slug}" (the tail of /title/ URLs); the hid is the part before the first '-'.
/// Pages are served plain (no tile scrambling), so ScrambleOffset stays 0.
/// </summary>
public class MangaFireSource(MangaFireBrowser browser) : ISource
{
    public string Name => "mangafire";
    public string DisplayName => "MangaFire";
    public string BaseUrl => "https://mangafire.to";
    public SourceCapabilities Capabilities =>
        SourceCapabilities.NeedsFlareSolverr | SourceCapabilities.SupportsLanguageFilter;

    public string? ResolveSeriesIdFromUrl(Uri url) =>
        // https://mangafire.to/title/{hid}-{slug}
        SourceUrl.PathTail(url, BaseUrl, "/title/", firstSegmentOnly: true);

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        using var json = JsonDocument.Parse(await browser.SearchAsync(title, ct));

        var results = new List<SourceSeriesResult>();
        foreach (var item in json.RootElement.GetProperty("items").EnumerateArray())
        {
            var url = item.GetProperty("url").GetString();   // "/title/{hid}-{slug}"
            var name = item.GetProperty("title").GetString();
            if (url is null || string.IsNullOrEmpty(name) || !url.StartsWith("/title/"))
            {
                continue;
            }

            var seriesId = url["/title/".Length..].Trim('/');
            results.Add(new SourceSeriesResult(seriesId, name, $"{BaseUrl}{url}", PosterFrom(item)));
        }

        return results;
    }

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        using var json = JsonDocument.Parse(await browser.SeriesAsync(sourceSeriesId, ct));
        var data = json.RootElement.GetProperty("data");

        var title = data.GetProperty("title").GetString() ?? sourceSeriesId;
        var description = data.TryGetProperty("synopsisHtml", out var synopsis)
            ? HtmlToText(synopsis.GetString())
            : null;
        var status = data.TryGetProperty("status", out var s) ? s.GetString() : null;

        return new SourceSeriesDetail(
            sourceSeriesId, title, $"{BaseUrl}/title/{sourceSeriesId}", PosterFrom(data), description, status);
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var language = string.IsNullOrWhiteSpace(languageFilter) ? "en" : languageFilter;

        var rawItems = await browser.ChaptersAsync(sourceSeriesId, language, ct);
        var chapters = new List<(SourceChapter Chapter, bool Official)>();
        foreach (var raw in rawItems)
        {
            using var doc = JsonDocument.Parse(raw);
            var item = doc.RootElement;

            var number = item.GetProperty("number");
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            var released = item.TryGetProperty("createdAt", out var created) &&
                           created.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(created.GetInt64()).UtcDateTime
                : (DateTime?)null;
            var official = item.TryGetProperty("type", out var type) && type.GetString() == "official";

            chapters.Add((new SourceChapter(
                Name,
                sourceSeriesId,
                item.GetProperty("id").GetInt64().ToString(),
                number.GetRawText(),
                number.GetDecimal(),
                Volume: null,
                Title: string.IsNullOrWhiteSpace(name) ? null : name,
                Language: language,
                ReleaseDate: released,
                Url: $"{BaseUrl}/title/{sourceSeriesId}"), official));
        }

        // The site lists official and unofficial rips of the same chapter as separate entries;
        // keep one row per number, preferring the official release.
        return SourceChapterList.Normalize(
            chapters, c => c.Chapter, g => g.OrderByDescending(c => c.Official).First());
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        using var json = JsonDocument.Parse(
            await browser.PagesAsync(chapter.SourceSeriesId, chapter.SourceChapterId, ct));

        // Page images live on a separate CDN that doesn't need the site's cookies; send only a Referer.
        var headers = new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" };
        var pages = json.RootElement.GetProperty("data").GetProperty("pages").EnumerateArray()
            .Select(p => p.GetProperty("url").GetString())
            .Where(url => !string.IsNullOrEmpty(url))
            .Select(url => new PageRequest(url!, headers))
            .ToList();

        return new ChapterPages(pages);
    }

    private static string? PosterFrom(JsonElement element) =>
        element.TryGetProperty("poster", out var poster) && poster.ValueKind == JsonValueKind.Object &&
        poster.TryGetProperty("medium", out var medium)
            ? medium.GetString()
            : null;

    private static string? HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var text = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", string.Empty);
        return WebUtility.HtmlDecode(text).Trim();
    }
}
