using System.Globalization;
using System.Text;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Maki.Core.Http;
using Maki.Core.Parsing;
using Maki.Core.Sources;

namespace Maki.Sources.Mangakakalot;

/// <summary>
/// MangaKakalot scraper. SSR-rendered pages behind Cloudflare — a plain client gets a challenge,
/// so every page goes through <see cref="IHtmlFetcher"/> (FlareSolverr on the first miss, cached
/// clearance after). The chapter list is the exception: it is not in the series HTML at all (the
/// page ships an empty <c>#chapter-list-container</c> and fills it from JS) but comes from
/// <c>/api/manga/{slug}/chapters</c>, which pages 50 at a time by default — a 658-chapter series
/// needs the offset walk in <see cref="ListChaptersAsync"/>.
/// Series id is the slug from <c>/manga/{slug}</c>.
/// </summary>
public class MangakakalotSource(IHtmlFetcher fetcher) : ISource
{
    private static readonly HtmlParser Parser = new();

    public string Name => "mangakakalot";
    public string DisplayName => "MangaKakalot";
    public string BaseUrl => "https://www.mangakakalot.gg";
    public SourceCapabilities Capabilities => SourceCapabilities.NeedsFlareSolverr;

    /// <summary>Both covers and page images are served from this CDN, and it 403s a missing Referer.</summary>
    public IReadOnlyList<string> CoverHosts => ["2xstorage.com"];

    /// <summary>Chapters the API returns per request; it caps out well above the 50 it defaults to.</summary>
    private const int ChapterPageSize = 100;

    /// <summary>Stops an offset walk that never sets has_more=false from looping forever.</summary>
    private const int MaxChapterPages = 100;

    public string? ResolveSeriesIdFromUrl(Uri url) =>
        SourceUrl.PathTail(url, BaseUrl, "/manga/", firstSegmentOnly: true);

    // ── Search ────────────────────────────────────────────────────────

    /// <summary>
    /// Search is a path, not a query string: /search/story/{keyword}, where the keyword is the title
    /// lowercased with every run of non-alphanumerics collapsed to a single underscore.
    /// </summary>
    internal static string SearchKeyword(string title)
    {
        var keyword = new StringBuilder(title.Length);
        foreach (var c in title.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                keyword.Append(c);
            }
            else if (keyword.Length > 0 && keyword[^1] != '_')
            {
                keyword.Append('_');
            }
        }

        return keyword.ToString().Trim('_');
    }

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var keyword = SearchKeyword(title);
        if (keyword.Length == 0)
        {
            return [];
        }

        // The site also has /home/search/json?searchword=, which returns the same hits as a compact
        // JSON array — but only to a request carrying XMLHttpRequest and form-urlencoded Content-Type
        // headers, and only when Cloudflare lets it through, which it does inconsistently. Neither
        // condition survives the FlareSolverr fallback (it fetches with its own browser's headers and
        // would hand back HTML where JSON was parsed), so the plain page is read instead: one shape,
        // whichever way the fetch went.
        var html = await fetcher.GetHtmlAsync($"{BaseUrl}/search/story/{keyword}", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var results = new List<SourceSeriesResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in doc.QuerySelectorAll(".panel_story_list .story_item"))
        {
            var link = item.QuerySelector("h3.story_name a");
            var href = link?.GetAttribute("href");
            if (link is null || string.IsNullOrEmpty(href))
            {
                continue;
            }

            var seriesId = SeriesIdFromHref(href);
            if (seriesId is null || !seen.Add(seriesId))
            {
                continue;
            }

            // The thumbnail img carries an unterminated alt attribute, which swallows the class and
            // sizing attributes that follow it — src comes first, so it survives; nothing after does.
            var cover = item.QuerySelector("img")?.GetAttribute("src");

            results.Add(new SourceSeriesResult(
                seriesId, link.TextContent.Trim(), $"{BaseUrl}/manga/{seriesId}", cover));
        }

        return results;
    }

    // ── Series detail ─────────────────────────────────────────────────

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var seriesId = NormalizeSeriesId(sourceSeriesId);
        var html = await fetcher.GetHtmlAsync($"{BaseUrl}/manga/{seriesId}", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var title = doc.QuerySelector("ul.manga-info-text h1")?.TextContent.Trim();
        var cover = doc.QuerySelector(".manga-info-pic img")?.GetAttribute("src");

        // The info list is unlabelled <li>s ("Status : Ongoing", "Author(s) : …"), so the field is
        // found by its prefix rather than by a class.
        var statusText = doc.QuerySelectorAll("ul.manga-info-text li")
            .Select(li => li.TextContent.Trim())
            .FirstOrDefault(t => t.StartsWith("Status", StringComparison.OrdinalIgnoreCase));

        var status = statusText switch
        {
            not null when statusText.Contains("Ongoing", StringComparison.OrdinalIgnoreCase) => "Ongoing",
            not null when statusText.Contains("Completed", StringComparison.OrdinalIgnoreCase) => "Completed",
            _ => null
        };

        return new SourceSeriesDetail(
            seriesId,
            string.IsNullOrEmpty(title) ? seriesId : title,
            $"{BaseUrl}/manga/{seriesId}",
            cover,
            Description(doc),
            status);
    }

    /// <summary>
    /// The synopsis is loose text in #contentBox, under an &lt;h2&gt; holding "{title} summary:".
    /// The block ends with the site's own "+ Other Manga" cross-promo lines, which aren't synopsis.
    /// </summary>
    private static string? Description(AngleSharp.Dom.IDocument doc)
    {
        var box = doc.QuerySelector("#contentBox");
        if (box is null)
        {
            return null;
        }

        foreach (var heading in box.QuerySelectorAll("h2"))
        {
            heading.Remove();
        }

        var lines = box.TextContent
            .Replace("​", "")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('+'));

        var description = string.Join("\n", lines).Trim();
        return description.Length == 0 ? null : description;
    }

    // ── Chapters ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var seriesId = NormalizeSeriesId(sourceSeriesId);
        var chapters = new List<SourceChapter>();

        for (var page = 0; page < MaxChapterPages; page++)
        {
            var offset = page * ChapterPageSize;
            var body = await fetcher.GetHtmlAsync(
                $"{BaseUrl}/api/manga/{seriesId}/chapters?offset={offset}&limit={ChapterPageSize}", ct);

            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("data", out var data))
            {
                break;
            }

            if (data.TryGetProperty("chapters", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in list.EnumerateArray())
                {
                    var chapter = ToChapter(seriesId, entry);
                    if (chapter is not null)
                    {
                        chapters.Add(chapter);
                    }
                }
            }

            var hasMore = data.TryGetProperty("pagination", out var pagination)
                          && pagination.TryGetProperty("has_more", out var more)
                          && more.ValueKind == JsonValueKind.True;
            if (!hasMore)
            {
                break;
            }
        }

        // The API lists newest first.
        return SourceChapterList.Normalize(chapters);
    }

    private SourceChapter? ToChapter(string seriesId, JsonElement entry)
    {
        var slug = entry.TryGetProperty("chapter_slug", out var s) ? s.GetString() : null;
        if (string.IsNullOrEmpty(slug))
        {
            return null;
        }

        var label = entry.TryGetProperty("chapter_name", out var n) ? n.GetString() : null;
        var parsed = ChapterNumberParser.Parse(label);

        // chapter_num is authoritative when present (it survives titles the parser can't read);
        // it is a JSON number and can be fractional for .5 chapters.
        decimal? number = entry.TryGetProperty("chapter_num", out var num) && num.ValueKind == JsonValueKind.Number
            ? num.GetDecimal()
            : parsed.Number;

        DateTime? releaseDate = entry.TryGetProperty("updated_at", out var updated)
                                && updated.ValueKind == JsonValueKind.String
                                && DateTime.TryParse(
                                    updated.GetString(), CultureInfo.InvariantCulture,
                                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : null;

        return new SourceChapter(
            Name,
            seriesId,
            slug,
            label,
            number,
            parsed.Volume,
            Title: null,
            Language: "en",
            releaseDate,
            Url: $"{BaseUrl}/manga/{seriesId}/{slug}");
    }

    // ── Page images ───────────────────────────────────────────────────

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var seriesId = NormalizeSeriesId(chapter.SourceSeriesId);
        var html = await fetcher.GetHtmlAsync(
            $"{BaseUrl}/manga/{seriesId}/{chapter.SourceChapterId}", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        // The CDN 403s a request with no Referer, so the header rides along to the downloader.
        var headers = new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" };

        var pages = doc.QuerySelectorAll(".container-chapter-reader img")
            .Select(img => img.GetAttribute("src"))
            .Where(src => !string.IsNullOrEmpty(src))
            .Select(src => new PageRequest(src!, headers))
            .ToList();

        return new ChapterPages(pages);
    }

    /// <summary>Accepts a bare slug or a full series URL, since older mappings stored the URL.</summary>
    private static string NormalizeSeriesId(string id)
    {
        if (!id.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return id.Trim('/');
        }

        return Uri.TryCreate(id, UriKind.Absolute, out var uri)
            ? SeriesIdFromHref(uri.AbsolutePath) ?? id
            : id;
    }

    /// <summary>Pulls "{slug}" out of a "/manga/{slug}" href, absolute or relative.</summary>
    private static string? SeriesIdFromHref(string href)
    {
        var path = Uri.TryCreate(href, UriKind.Absolute, out var uri) ? uri.AbsolutePath : href;
        const string marker = "/manga/";

        var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var tail = path[(index + marker.Length)..].Trim('/').Split('/')[0];
        return tail.Length == 0 ? null : tail;
    }
}
