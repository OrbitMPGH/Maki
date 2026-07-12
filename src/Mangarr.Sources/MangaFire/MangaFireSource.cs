using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mangarr.Core.Http;
using Mangarr.Core.Sources;

namespace Mangarr.Sources.MangaFire;

/// <summary>
/// MangaFire scraper, built on the site's JSON API (mangafire.to is a React SPA whose
/// HTML pages are an empty shell — scraping markup gets you the home page). The site
/// sits behind an anti-bot challenge, so everything goes through ChallengeAwareFetcher
/// (direct-with-cookies first, FlareSolverr on challenge). Endpoints:
///   /api/titles?keyword={q}      → search:   {items:[{hid, title, poster, url}]}
///   /api/titles/{hid}            → detail:   {data:{title, synopsisHtml, status, ...}}
///   /api/titles/{hid}/chapters   → chapters: {items:[{id, number, name, language, type, createdAt}]}
///   /api/chapters/{id}           → pages:    {data:{pages:[{url, width, height}]}}
/// Series id is stored as "{hid}-{slug}" (the tail of /title/ URLs); the hid is the part
/// before the first '-'. Unlike the retired mangafire markup, pages are served plain
/// (no tile scrambling), so ScrambleOffset stays 0.
/// </summary>
public class MangaFireSource(ChallengeAwareFetcher fetcher) : ISource
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
        using var json = await GetJsonAsync(
            $"{BaseUrl}/api/titles?keyword={Uri.EscapeDataString(title)}&limit=30", ct);

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
        using var json = await GetJsonAsync($"{BaseUrl}/api/titles/{HidFrom(sourceSeriesId)}", ct);
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

        // The endpoint is paginated (limit is capped at 200 server-side).
        var chapters = new List<(SourceChapter Chapter, bool Official)>();
        for (var page = 1; page <= 100; page++)
        {
            using var json = await GetJsonAsync(
                $"{BaseUrl}/api/titles/{HidFrom(sourceSeriesId)}/chapters" +
                $"?language={Uri.EscapeDataString(language)}&limit=200&page={page}", ct);

            foreach (var item in json.RootElement.GetProperty("items").EnumerateArray())
            {
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

            var hasNext = json.RootElement.TryGetProperty("meta", out var meta) &&
                          meta.TryGetProperty("hasNext", out var next) && next.GetBoolean();
            if (!hasNext)
            {
                break;
            }
        }

        // The site lists official and unofficial rips of the same chapter as separate
        // entries; keep one row per number, preferring the official release.
        return chapters
            .GroupBy(c => c.Chapter.Number)
            .Select(g => g.OrderByDescending(c => c.Official).First().Chapter)
            .OrderBy(c => c.Number)
            .ToList();
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        using var json = await GetJsonAsync($"{BaseUrl}/api/chapters/{chapter.SourceChapterId}", ct);

        // Page images live on a separate CDN that doesn't need the site's cookies;
        // send only a Referer.
        var headers = new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" };
        var pages = json.RootElement.GetProperty("data").GetProperty("pages").EnumerateArray()
            .Select(p => p.GetProperty("url").GetString())
            .Where(url => !string.IsNullOrEmpty(url))
            .Select(url => new PageRequest(url!, headers))
            .ToList();

        return new ChapterPages(pages);
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        var body = await fetcher.GetHtmlAsync(url, ct);
        return JsonDocument.Parse(UnwrapJson(body));
    }

    /// <summary>
    /// FlareSolverr renders JSON endpoints in a browser, so the payload comes back
    /// HTML-escaped inside a &lt;pre&gt; shell; direct fetches return raw JSON.
    /// </summary>
    private static string UnwrapJson(string body)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            return body;
        }

        var match = Regex.Match(body, @"<pre[^>]*>(.*)</pre>", RegexOptions.Singleline);
        if (!match.Success)
        {
            throw new InvalidOperationException("MangaFire returned neither JSON nor a FlareSolverr-wrapped payload");
        }

        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static string HidFrom(string sourceSeriesId)
    {
        var dash = sourceSeriesId.IndexOf('-');
        return dash > 0 ? sourceSeriesId[..dash] : sourceSeriesId;
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
