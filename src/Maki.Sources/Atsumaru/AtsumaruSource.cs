using System.Text.Json;
using Maki.Core.Sources;

namespace Maki.Sources.Atsumaru;

/// <summary>
/// Atsumaru (atsu.moe) source. The site is a React SPA over a JSON API at /api, so every
/// call here is JSON — no markup parsing and no Cloudflare challenge. Series ids are short
/// opaque strings ("94bKW"), chapter ids likewise ("l4Sdzg4h"); a chapter is only addressable
/// together with its series, which is why <see cref="GetPagesAsync"/> passes both.
/// <para>
/// Images (posters and pages alike) live on cdn.atsu.moe and are referenced by site-relative
/// path, sometimes with the leading "/static" already on it and sometimes without, so every
/// path goes through <see cref="ImageUrl"/> rather than being concatenated at the call site.
/// The CDN is a subdomain of the site's own domain, so the cover proxy allows it without a
/// <see cref="ISource.CoverHosts"/> entry.
/// </para>
/// <para>
/// Atsumaru carries several scanlation groups per series and lists every group's chapters in
/// one flat array, so a 200-chapter series can arrive as 800 rows. See
/// <see cref="ListChaptersAsync"/> for how one group is picked.
/// </para>
/// </summary>
public class AtsumaruSource(IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-atsumaru";

    /// <summary>Where poster and page images are served from; a subdomain of <see cref="BaseUrl"/>.</summary>
    private const string CdnBaseUrl = "https://cdn.atsu.moe";

    /// <summary>
    /// The search index is Typesense, which requires the caller to name the fields to match on
    /// (it 400s with "No search fields specified" otherwise). These are the site's own three
    /// title fields and weights; the arrays are positional, so all three must stay the same length.
    /// </summary>
    private const string SearchFields = "title,englishTitle,otherNames";

    public string Name => "atsumaru";
    public string DisplayName => "Atsumaru";
    public string BaseUrl => "https://atsu.moe";
    public SourceCapabilities Capabilities => SourceCapabilities.None;

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    public string? ResolveSeriesIdFromUrl(Uri url) =>
        // https://atsu.moe/manga/{id}, or a /read/{id}/{chapterId} link copied mid-chapter.
        SourceUrl.PathTail(url, BaseUrl, "/manga/", firstSegmentOnly: true)
        ?? SourceUrl.PathTail(url, BaseUrl, "/read/", firstSegmentOnly: true);

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        // medium:=Comic drops the prose novels the same index holds — they have no page images.
        // hidden:!=true is what the site's own search sends; hidden rows are unreadable.
        var filter = Uri.EscapeDataString("medium:=Comic && hidden:!=true");
        var root = await GetAsync(
            $"search/manga?q={Uri.EscapeDataString(title)}" +
            $"&query_by={SearchFields}&query_by_weights=4,3,2&num_typos=2,2,1&prefix=true,true,true" +
            "&include_fields=id,title,englishTitle,poster,posterMedium,synopsis" +
            $"&filter_by={filter}&per_page=20",
            ct);

        if (!root.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<SourceSeriesResult>();
        foreach (var hit in hits.EnumerateArray())
        {
            var document = hit.TryGetProperty("document", out var d) ? d : hit;
            var id = String(document, "id");
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            var name = String(document, "title") ?? String(document, "englishTitle") ?? "Unknown";
            var cover = ImageUrl(String(document, "posterMedium") ?? String(document, "poster"));

            results.Add(new SourceSeriesResult(
                id, name, SeriesUrl(id), cover, String(document, "synopsis")));
        }

        return results;
    }

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var root = await GetAsync($"manga/page?id={Uri.EscapeDataString(sourceSeriesId)}", ct);
        var page = root.TryGetProperty("mangaPage", out var p) ? p : root;

        string? cover = null;
        if (page.TryGetProperty("poster", out var poster) && poster.ValueKind == JsonValueKind.Object)
        {
            cover = ImageUrl(String(poster, "mediumImage") ?? String(poster, "image"));
        }

        return new SourceSeriesDetail(
            sourceSeriesId,
            String(page, "title") ?? String(page, "englishTitle") ?? sourceSeriesId,
            SeriesUrl(sourceSeriesId),
            cover,
            String(page, "synopsis"),
            String(page, "status"));
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        // manga/info carries the same chapter array the series page shows but tags each row with
        // its scanlation group (scanId), which is what makes picking between duplicates possible.
        var root = await GetAsync($"manga/info?mangaId={Uri.EscapeDataString(sourceSeriesId)}", ct);
        if (!root.TryGetProperty("chapters", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var listed = new List<ListedChapter>();
        foreach (var row in rows.EnumerateArray())
        {
            var id = String(row, "id");
            if (string.IsNullOrEmpty(id) ||
                !row.TryGetProperty("number", out var numberEl) ||
                numberEl.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            // The API gives the number already parsed, and it is genuinely fractional for
            // interstitial chapters (Berserk has 99.5), so take it as-is rather than
            // re-deriving one from the title.
            listed.Add(new ListedChapter(
                id,
                numberEl.GetDecimal(),
                numberEl.GetRawText(),
                String(row, "title"),
                String(row, "scanId") ?? string.Empty));
        }

        // Every group's chapters arrive in one array, so most numbers appear several times.
        // Prefer the group that covers the most of the series and break ties on the id, so the
        // whole series reads as one group's work instead of hopping between them chapter to
        // chapter — a page-count or recency rule would pick a different winner per chapter.
        var coverage = listed
            .GroupBy(c => c.ScanId)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return SourceChapterList.Normalize(
            listed,
            c => new SourceChapter(
                Name,
                sourceSeriesId,
                c.Id,
                c.NumberRaw,
                c.Number,
                Volume: null,
                Title: string.IsNullOrWhiteSpace(c.Title) ? null : c.Title,
                // Atsumaru hosts English scans only and states no language per chapter.
                Language: "en",
                ReleaseDate: null,
                Url: $"{BaseUrl}/read/{sourceSeriesId}/{c.Id}"),
            group => group
                .OrderByDescending(c => coverage.GetValueOrDefault(c.ScanId))
                .ThenBy(c => c.ScanId, StringComparer.Ordinal)
                .First());
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var root = await GetAsync(
            $"read/chapter?mangaId={Uri.EscapeDataString(chapter.SourceSeriesId)}" +
            $"&chapterId={Uri.EscapeDataString(chapter.SourceChapterId)}",
            ct);

        if (!root.TryGetProperty("readChapter", out var read) ||
            !read.TryGetProperty("pages", out var pageArray) ||
            pageArray.ValueKind != JsonValueKind.Array)
        {
            return new ChapterPages([]);
        }

        var headers = new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" };
        var pages = new List<PageRequest>();
        foreach (var page in pageArray
                     .EnumerateArray()
                     .OrderBy(p => p.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number
                         ? n.GetInt32()
                         : int.MaxValue))
        {
            var url = ImageUrl(String(page, "image"));
            if (url is not null)
            {
                pages.Add(new PageRequest(url, headers));
            }
        }

        return new ChapterPages(pages);
    }

    private string SeriesUrl(string sourceSeriesId) => $"{BaseUrl}/manga/{sourceSeriesId}";

    /// <summary>
    /// Turns an image path into a CDN URL. The API is inconsistent about the "/static" prefix —
    /// search hits carry it, the series page's poster object doesn't — so normalise both ways
    /// rather than trusting either.
    /// </summary>
    private static string? ImageUrl(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        var relative = path.TrimStart('/');
        if (!relative.StartsWith("static/", StringComparison.OrdinalIgnoreCase))
        {
            relative = $"static/{relative}";
        }

        return $"{CdnBaseUrl}/{relative}";
    }

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        var body = await Client.GetStringAsync(path, ct);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    /// <summary>A chapter row as listed, before duplicates across scanlation groups are resolved.</summary>
    private record ListedChapter(string Id, decimal Number, string NumberRaw, string? Title, string ScanId);
}
