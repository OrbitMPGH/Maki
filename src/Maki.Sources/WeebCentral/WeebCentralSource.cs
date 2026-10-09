using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Maki.Core.Parsing;
using Maki.Core.Sources;

namespace Maki.Sources.WeebCentral;

/// <summary>
/// Weeb Central scraper. The site is HTMX-driven, so we hit the partial-HTML
/// endpoints directly (search/data, full-chapter-list, chapter images).
/// Series id is stored as "{ULID}/{slug}".
/// </summary>
public partial class WeebCentralSource(IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-weebcentral";

    private static readonly HtmlParser Parser = new();

    public string Name => "weebcentral";
    public string DisplayName => "Weeb Central";
    public string BaseUrl => "https://weebcentral.com";
    public SourceCapabilities Capabilities => SourceCapabilities.None;
    public SourceContent Content => SourceContent.Manga | SourceContent.Manhwa;
    public IReadOnlyList<string> CoverHosts => ["temp.compsci88.com"];

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    // The site renders every numbered entry as "<word> <number>", and the word is picked per series:
    // "Chapter 12", "Episode 12", "Plot 49", "Mischief 225". Nothing else in the listing carries the
    // number, so the trailing number is the chapter whatever the word is.
    [GeneratedRegex(@"\s(\d+(?:\.\d+)?)\s*$")]
    private static partial Regex TrailingNumberRegex();

    internal static ParsedChapter ParseLabel(string label)
    {
        var parsed = ChapterNumberParser.Parse(label);
        if (parsed.Number is not null || parsed.Volume is not null)
        {
            return parsed;
        }

        var trailing = TrailingNumberRegex().Match(label);
        return trailing.Success
            ? new ParsedChapter(decimal.Parse(trailing.Groups[1].Value, CultureInfo.InvariantCulture), null, false)
            : parsed;
    }

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var html = await Client.GetStringAsync(
            $"search/data?limit=32&offset=0&text={Uri.EscapeDataString(title)}" +
            "&sort=Best%20Match&order=Ascending&official=Any&display_mode=Full%20Display",
            ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        // Each result renders two anchors for the same series: a card wrapper
        // (image + badges) and a clean title link (class "link"). Index covers
        // from the wrappers, take titles from the title links.
        var covers = new Dictionary<string, string>();
        var results = new List<SourceSeriesResult>();
        var seen = new HashSet<string>();

        foreach (var link in doc.QuerySelectorAll("a[href*='/series/']"))
        {
            var seriesId = SeriesIdFrom(link.GetAttribute("href")!);
            var cover = link.QuerySelector("img")?.GetAttribute("src");
            if (cover != null && !covers.ContainsKey(seriesId))
            {
                covers[seriesId] = cover;
            }
        }

        foreach (var link in doc.QuerySelectorAll("a.link[href*='/series/'], a.link-hover[href*='/series/']"))
        {
            var href = link.GetAttribute("href")!;
            var seriesId = SeriesIdFrom(href);
            var name = link.TextContent.Trim();

            if (string.IsNullOrEmpty(name) || !seen.Add(seriesId))
            {
                continue;
            }

            results.Add(new SourceSeriesResult(seriesId, name, href, covers.GetValueOrDefault(seriesId)));
        }

        return results;
    }

    private static string SeriesIdFrom(string href)
    {
        const string marker = "/series/";
        return href[(href.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
    }

    public string? ResolveSeriesIdFromUrl(Uri url) =>
        // https://weebcentral.com/series/{id}/{slug} — the id spans both segments
        SourceUrl.PathTail(url, BaseUrl, "/series/");

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var html = await Client.GetStringAsync($"series/{sourceSeriesId}", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var title = doc.QuerySelector("h1")?.TextContent.Trim() ?? sourceSeriesId;
        var description = doc.QuerySelector("li p.whitespace-pre-wrap, p.whitespace-pre-wrap")?.TextContent.Trim();
        return new SourceSeriesDetail(sourceSeriesId, title, $"{BaseUrl}/series/{sourceSeriesId}", null, description);
    }

    /// <summary>
    /// WeebCentral renders a row of tracker icons on the series page (AniList and MangaUpdates; it
    /// links no MyAnimeList entry). They are plain anchors, so the ids come out of the hrefs.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>?> GetExternalIdsAsync(
        string sourceSeriesId, CancellationToken ct = default)
    {
        var html = await Client.GetStringAsync($"series/{sourceSeriesId}", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        // Read every outbound link on the page rather than the tracker row's own markup: the row is
        // styled with utility classes that change with the site's theme, while the hrefs it holds are
        // the only thing that has to stay put for the links to work at all.
        var links = doc.QuerySelectorAll("a[href]")
            .Select(a => a.GetAttribute("href"))
            .Where(href => href != null && href.StartsWith("http", StringComparison.OrdinalIgnoreCase));

        return SourceExternalIds.FromUrls(links);
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var ulid = sourceSeriesId.Split('/')[0];
        var html = await Client.GetStringAsync($"series/{ulid}/full-chapter-list", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var chapters = new List<SourceChapter>();
        foreach (var link in doc.QuerySelectorAll("a[href*='/chapters/']"))
        {
            var href = link.GetAttribute("href")!;
            var marker = "/chapters/";
            var chapterId = href[(href.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];

            var label = link.QuerySelector("span.grow > span")?.TextContent.Trim()
                        ?? link.TextContent.Trim();
            var parsed = ParseLabel(label);

            DateTime? releaseDate = null;
            var time = link.QuerySelector("time")?.GetAttribute("datetime");
            if (time != null && DateTime.TryParse(
                    time, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            {
                releaseDate = dt;
            }

            // A null Number is deduped by title downstream (ChapterIdentity), so it must never
            // carry a null Title too, or two different specials read as one chapter.
            var title = parsed.Number is null ? label : null;

            chapters.Add(new SourceChapter(
                Name,
                sourceSeriesId,
                chapterId,
                label,
                parsed.Number,
                parsed.Volume,
                title,
                Language: "en",
                releaseDate,
                href));
        }

        return SourceChapterList.Normalize(chapters);
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var html = await Client.GetStringAsync(
            $"chapters/{chapter.SourceChapterId}/images?is_prev=False&current_page=1&reading_style=long_strip",
            ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var headers = new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" };
        var pages = doc.QuerySelectorAll("img")
            .Select(img => img.GetAttribute("src"))
            .Where(src => !string.IsNullOrEmpty(src) && src!.StartsWith("http", StringComparison.Ordinal))
            .Select(src => new PageRequest(src!, headers))
            .ToList();

        if (pages.Count == 0)
        {
            throw new InvalidOperationException($"No page images found for WeebCentral chapter {chapter.SourceChapterId}");
        }

        return new ChapterPages(pages);
    }
}
