using System.Globalization;
using System.Net;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Maki.Core.Http;
using Maki.Core.Parsing;
using Maki.Core.Sources;

namespace Maki.Sources.Rawkuma;

/// <summary>
/// Rawkuma scraper. Japanese raw aggregator on the NatsuId WordPress theme: search and series
/// detail come from the WP REST API (<c>/wp-json/wp/v2/manga</c>), the chapter list is an htmx
/// fragment from <c>admin-ajax.php</c>, and pages are read off the chapter page's markup. Series id
/// is the WordPress slug; <see cref="ListChaptersAsync"/> re-fetches the series detail to recover the
/// numeric post id the chapter-list endpoint needs, since the slug alone does not carry it.
/// Domain-rotating: <see cref="BaseUrl"/> honours <c>MAKI_SOURCE_RAWKUMA_BASEURL</c>.
/// </summary>
public class RawkumaSource(IHtmlFetcher fetcher, string? baseUrlOverride = null) : ISource
{
    private const string DefaultBaseUrl = "https://rawkuma.net";

    private static readonly HtmlParser Parser = new();

    /// <summary>
    /// <paramref name="baseUrlOverride"/> takes precedence over the env var; it exists so a unit test
    /// can point at a mirror without touching process environment variables. DI never supplies it
    /// (nothing registers a bare <c>string</c>), so the constructor falls back to its default.
    /// </summary>
    private readonly string _baseUrl =
        (baseUrlOverride ?? Environment.GetEnvironmentVariable("MAKI_SOURCE_RAWKUMA_BASEURL"))?.TrimEnd('/')
            ?? DefaultBaseUrl;

    public string Name => "rawkuma";
    public string DisplayName => "Rawkuma";
    public string BaseUrl => _baseUrl;
    public SourceCapabilities Capabilities => SourceCapabilities.NeedsFlareSolverr;
    public IReadOnlyList<string> SupportedLanguages => ["ja"];

    public string? ResolveSeriesIdFromUrl(Uri url)
    {
        var tail = SourceUrl.PathTail(url, BaseUrl, "/manga/");
        if (tail is null)
        {
            return null;
        }

        // A chapter URL names the series first too ("one-piece/chapter-1193.407873"), so only a
        // bare one-segment tail is a series page; anything longer is a chapter and must not resolve.
        var segments = tail.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 1 ? segments[0] : null;
    }

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/wp-json/wp/v2/manga?search={Uri.EscapeDataString(title)}" +
                  "&orderby=relevance&per_page=20&_fields=id,slug,title,link,meta,content";
        var body = await fetcher.GetHtmlAsync(url, ct);
        var root = ParseJson(body, url);
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"Unexpected response from {url}: {Truncate(body)}");
        }

        var results = new List<SourceSeriesResult>();
        foreach (var item in root.EnumerateArray())
        {
            // Novels share the same manga post type; they are not readable here.
            if (TaxName(item, "type") == "Novel")
            {
                continue;
            }

            var slug = GetString(item, "slug");
            if (string.IsNullOrEmpty(slug))
            {
                continue;
            }

            var name = WebUtility.HtmlDecode(TitleRendered(item) ?? slug);
            var link = Rebase(GetString(item, "link")) ?? $"{BaseUrl}/manga/{slug}/";
            var cover = Rebase(MetaValue(item, "thumbnail"));

            results.Add(new SourceSeriesResult(slug, name, link, cover, ExternalIds: SearchExternalIds(item)));
        }

        return results;
    }

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var item = await GetSeriesItemAsync(sourceSeriesId, ct);

        var name = WebUtility.HtmlDecode(TitleRendered(item) ?? sourceSeriesId);
        var link = Rebase(GetString(item, "link")) ?? $"{BaseUrl}/manga/{sourceSeriesId}/";
        var cover = Rebase(MetaValue(item, "thumbnail"));
        var description = item.TryGetProperty("content", out var content) &&
                           content.TryGetProperty("rendered", out var rendered) &&
                           rendered.ValueKind == JsonValueKind.String
            ? Parser.ParseDocument(rendered.GetString() ?? string.Empty).Body?.TextContent.Trim()
            : null;

        return new SourceSeriesDetail(sourceSeriesId, name, link, cover, description, MapStatus(TaxName(item, "status")));
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var item = await GetSeriesItemAsync(sourceSeriesId, ct);
        var mangaId = item.GetProperty("id").GetInt64();

        var html = await fetcher.GetHtmlAsync(
            $"{BaseUrl}/wp-admin/admin-ajax.php?manga_id={mangaId}&page=99&action=chapter_list", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var chapters = new List<SourceChapter>();
        foreach (var row in doc.QuerySelectorAll("div[data-chapter-number]"))
        {
            var href = Rebase(row.QuerySelector("a[href]")?.GetAttribute("href"));
            var chapterId = LastPathSegment(href);
            if (chapterId is null)
            {
                continue;
            }

            var numberRaw = row.GetAttribute("data-chapter-number");
            var number = decimal.TryParse(numberRaw, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var direct)
                ? direct
                : ChapterNumberParser.Parse(row.QuerySelector("span")?.TextContent.Trim()).Number;

            var releaseDate = ParseReleaseDate(row.QuerySelector("time[datetime]")?.GetAttribute("datetime"));

            chapters.Add(new SourceChapter(
                Name,
                sourceSeriesId,
                chapterId,
                numberRaw,
                number,
                Volume: null,
                Title: null,
                Language: "ja",
                releaseDate,
                Url: href));
        }

        return SourceChapterList.Normalize(chapters);
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var url = Rebase(chapter.Url) ?? $"{BaseUrl}/manga/{chapter.SourceSeriesId}/{chapter.SourceChapterId}/";
        var html = await fetcher.GetHtmlAsync(url, ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        // Page images live on kuma.kyut.dev, never on the rotating BaseUrl; leave their host alone.
        var headers = new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" };
        var pages = doc.QuerySelectorAll("section[data-image-data] > img[src]")
            .Select(img => img.GetAttribute("src"))
            .Where(src => !string.IsNullOrEmpty(src))
            .Select(src => new PageRequest(src!, headers))
            .ToList();

        if (pages.Count == 0)
        {
            // Usually a chapter still being uploaded, not a broken source; let the pipeline retry.
            throw new ChapterLockedException(
                $"Rawkuma chapter {chapter.SourceChapterId} of {chapter.SourceSeriesId} has no pages yet");
        }

        return new ChapterPages(pages);
    }

    private async Task<JsonElement> GetSeriesItemAsync(string slug, CancellationToken ct)
    {
        var url = $"{BaseUrl}/wp-json/wp/v2/manga?slug={Uri.EscapeDataString(slug)}&_fields=id,slug,title,link,meta,content";
        var body = await fetcher.GetHtmlAsync(url, ct);
        var root = ParseJson(body, url);
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"Unexpected response from {url}: {Truncate(body)}");
        }

        foreach (var item in root.EnumerateArray())
        {
            return item;
        }

        throw new KeyNotFoundException($"No Rawkuma manga found for slug '{slug}'");
    }

    private static IReadOnlyDictionary<string, string>? SearchExternalIds(JsonElement item)
    {
        var raw = MetaValue(item, "mangaupdates_id");
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0
            ? SourceExternalIds.From((ExternalIdService.MangaUpdates, ToBase36(id)))
            : null;
    }

    /// <summary>MangaBaka stores MangaUpdates ids as a lowercase base36 slug (e.g. "pb8uwds" for One Piece).</summary>
    internal static string ToBase36(long value)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        if (value <= 0)
        {
            return "0";
        }

        var chars = new Stack<char>();
        while (value > 0)
        {
            chars.Push(digits[(int)(value % 36)]);
            value /= 36;
        }

        return new string(chars.ToArray());
    }

    private static string? MapStatus(string? raw) => raw switch
    {
        "Ongoing" => "Ongoing",
        "Completed" => "Completed",
        "Cancelled" => "Cancelled",
        "On Hiatus" => "Hiatus",
        _ => null
    };

    private static string? TitleRendered(JsonElement item) =>
        item.TryGetProperty("title", out var title) &&
        title.TryGetProperty("rendered", out var rendered) &&
        rendered.ValueKind == JsonValueKind.String
            ? rendered.GetString()
            : null;

    private static string? GetString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? MetaValue(JsonElement item, string key) =>
        item.TryGetProperty("meta", out var meta) &&
        meta.TryGetProperty("meta", out var metaMeta) &&
        metaMeta.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The first tax entry's name for a taxonomy ("type" -> "Manga"/"Novel", "status" -> "Ongoing", ...).</summary>
    private static string? TaxName(JsonElement item, string taxonomy)
    {
        if (!item.TryGetProperty("meta", out var meta) ||
            !meta.TryGetProperty("tax", out var tax) ||
            tax.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var entry in tax.EnumerateArray())
        {
            if (entry.TryGetProperty("taxonomy", out var t) && t.GetString() == taxonomy &&
                entry.TryGetProperty("name", out var name))
            {
                return name.GetString();
            }
        }

        return null;
    }

    /// <summary>
    /// WordPress always serialises the site's canonical host in <c>link</c>, cover thumbnails and
    /// chapter hrefs, even when the configured <see cref="BaseUrl"/> points at a mirror. Rewrite a
    /// canonical-host URL onto the configured host so a mirror override actually takes effect; a URL
    /// on any other host (page images on kuma.kyut.dev) passes through untouched.
    /// </summary>
    private string? Rebase(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return url;
        }

        if (!IsDefaultHost(uri.Host) ||
            !Uri.TryCreate(BaseUrl, UriKind.Absolute, out var baseUri) ||
            IsDefaultHost(baseUri.Host))
        {
            return url;
        }

        return $"{BaseUrl}{uri.PathAndQuery}{uri.Fragment}";
    }

    private static bool IsDefaultHost(string host) =>
        host.Equals("rawkuma.net", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("www.rawkuma.net", StringComparison.OrdinalIgnoreCase);

    private static string? LastPathSegment(string? href)
    {
        if (string.IsNullOrEmpty(href) || !Uri.TryCreate(href, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 ? segments[^1] : null;
    }

    private static DateTime? ParseReleaseDate(string? datetime) =>
        DateTime.TryParse(
            datetime, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// JSON fetched through FlareSolverr comes back wrapped in an HTML shell
    /// (&lt;html&gt;&lt;body&gt;&lt;pre&gt;...&lt;/pre&gt;&lt;/body&gt;&lt;/html&gt;); the direct path
    /// answers plain JSON. Detect which one we got and parse either way.
    /// </summary>
    private static JsonElement ParseJson(string body, string url)
    {
        var trimmed = body.TrimStart();
        var json = trimmed.StartsWith('<') ? UnwrapPre(trimmed, url) : body;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"Unexpected response from {url}: {Truncate(body)}");
        }
    }

    private static string UnwrapPre(string html, string url)
    {
        var pre = Parser.ParseDocument(html).QuerySelector("pre");
        if (pre is null)
        {
            throw new InvalidOperationException($"Unexpected response from {url}: {Truncate(html)}");
        }

        return pre.TextContent;
    }

    private static string Truncate(string body) => body.Length <= 100 ? body : body[..100];
}
