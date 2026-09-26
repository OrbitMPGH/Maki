using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Maki.Core.Parsing;
using Maki.Core.Sources;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace Maki.Sources.MangaDenizi;

/// <summary>
/// MangaDenizi source: Turkish manga/manhwa aggregator, a Laravel JSON API (no HTML parsing).
/// Every reader page is tile-scrambled ("tiled-v1"), so GetPagesAsync fetches and descrambles
/// each page itself through the Data hatch (see MangaDeniziDescrambler) and re-encodes it as JPEG.
/// </summary>
public class MangaDeniziSource(IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-mangadenizi";

    private static readonly Uri ApiReferer = new("https://mangadenizi.net/manga");
    private const int MaxSearchPages = 3;

    public string Name => "mangadenizi";
    public string DisplayName => "MangaDenizi";
    public string BaseUrl => "https://mangadenizi.net";
    public SourceCapabilities Capabilities => SourceCapabilities.None;
    public IReadOnlyList<string> SupportedLanguages => ["tr"];
    public IReadOnlyList<string> CoverHosts => [];

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    public string? ResolveSeriesIdFromUrl(Uri url)
    {
        // /api/v1/web/manga/<slug> contains the same "/manga/" marker as the series page, so
        // only a path that actually starts with it counts.
        if (!url.AbsolutePath.StartsWith("/manga/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return SourceUrl.PathTail(url, BaseUrl, "/manga/", firstSegmentOnly: true);
    }

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var results = new List<SourceSeriesResult>();

        for (var page = 1; page <= MaxSearchPages; page++)
        {
            var response = await GetJsonAsync(
                $"api/v1/web/manga?page={page}&q={Uri.EscapeDataString(title)}", ct);
            var data = RequireProperty(response, "data");
            var paginator = RequireProperty(data, response, "manga");
            var items = RequireProperty(paginator, response, "data");
            if (items.ValueKind != JsonValueKind.Array)
            {
                throw Unexpected(response);
            }

            foreach (var item in items.EnumerateArray())
            {
                var slug = GetString(item, "slug");
                if (string.IsNullOrEmpty(slug))
                {
                    continue;
                }

                var seriesTitle = GetString(item, "title") ?? slug;
                var cover = GetString(item, "cover_url") ?? GetString(item, "cover_thumb_url");
                var description = GetString(item, "description");
                results.Add(new SourceSeriesResult(slug, seriesTitle, $"{BaseUrl}/manga/{slug}", cover, description));
            }

            var currentPage = GetInt(paginator, "current_page") ?? page;
            var lastPage = GetInt(paginator, "last_page") ?? currentPage;
            if (currentPage >= lastPage)
            {
                break;
            }
        }

        return results;
    }

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var (manga, _) = await GetMangaAsync(sourceSeriesId, ct);

        var title = GetString(manga, "title") ?? sourceSeriesId;
        var cover = GetString(manga, "cover_url") ?? GetString(manga, "cover_thumb_url");
        var description = GetString(manga, "description");
        var status = GetString(manga, "status");

        return new SourceSeriesDetail(sourceSeriesId, title, $"{BaseUrl}/manga/{sourceSeriesId}", cover, description, status);
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var (manga, response) = await GetMangaAsync(sourceSeriesId, ct);
        if (!manga.TryGetProperty("chapters", out var chaptersEl) || chaptersEl.ValueKind != JsonValueKind.Array)
        {
            throw Unexpected(response);
        }

        var chapters = new List<SourceChapter>();
        foreach (var row in chaptersEl.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object ||
                !row.TryGetProperty("number", out var numberEl) ||
                !row.TryGetProperty("slug", out var slugEl))
            {
                continue;
            }

            var chapterSlug = slugEl.GetString();
            if (string.IsNullOrEmpty(chapterSlug))
            {
                continue;
            }

            var rawNumber = numberEl.ValueKind switch
            {
                JsonValueKind.Number => numberEl.GetRawText(),
                JsonValueKind.String => numberEl.GetString(),
                _ => null
            };

            var parsed = ChapterNumberParser.Parse(rawNumber);
            var chapterTitle = GetString(row, "title");
            var releaseDate = ParseDate(GetString(row, "published_at"));

            chapters.Add(new SourceChapter(
                Name,
                sourceSeriesId,
                $"{sourceSeriesId}/{chapterSlug}",
                rawNumber,
                parsed.Number,
                parsed.Volume,
                Title: string.IsNullOrWhiteSpace(chapterTitle) ? null : chapterTitle,
                Language: "tr",
                ReleaseDate: releaseDate,
                Url: $"{BaseUrl}/read/{sourceSeriesId}/{chapterSlug}"));
        }

        return SourceChapterList.Normalize(chapters);
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var separator = chapter.SourceChapterId.LastIndexOf('/');
        if (separator < 0)
        {
            throw new InvalidOperationException($"Malformed MangaDenizi chapter id '{chapter.SourceChapterId}'");
        }

        var mangaSlug = chapter.SourceChapterId[..separator];
        var chapterSlug = chapter.SourceChapterId[(separator + 1)..];

        var response = await GetJsonAsync($"api/v1/reader/{mangaSlug}/{chapterSlug}", ct);
        var pagesEl = RequireProperty(response, "pages");
        if (pagesEl.ValueKind != JsonValueKind.Array)
        {
            throw Unexpected(response);
        }

        if (pagesEl.GetArrayLength() == 0)
        {
            throw new ChapterLockedException($"Chapter {chapterSlug} of {mangaSlug} has no pages");
        }

        var headers = new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" };
        var pages = new List<PageRequest>();
        foreach (var pageEl in pagesEl.EnumerateArray())
        {
            var imageUrl = GetString(pageEl, "image_url");
            if (string.IsNullOrEmpty(imageUrl))
            {
                continue;
            }

            var raw = await FetchImageBytesAsync(imageUrl, ct);
            var data = await ProcessPageAsync(raw, pageEl, imageUrl, ct);
            pages.Add(new PageRequest(imageUrl, headers, Data: data));
        }

        return new ChapterPages(pages);
    }

    /// <summary>
    /// Descrambles one page's already-fetched bytes if it carries a "tiled-v1" scramble, or
    /// passes them through unchanged. An unrecognised non-null method throws rather than ever
    /// writing a scrambled page to disk.
    /// </summary>
    internal static async Task<byte[]> ProcessPageAsync(byte[] raw, JsonElement page, string url, CancellationToken ct)
    {
        if (!page.TryGetProperty("scramble", out var scrambleEl) || scrambleEl.ValueKind == JsonValueKind.Null)
        {
            return raw;
        }

        var method = scrambleEl.TryGetProperty("method", out var methodEl) && methodEl.ValueKind == JsonValueKind.String
            ? methodEl.GetString()
            : null;

        if (method is null)
        {
            return raw;
        }

        if (method != "tiled-v1")
        {
            throw new NotSupportedException($"Unknown MangaDenizi scramble method '{method}' for page {url}");
        }

        if (!scrambleEl.TryGetProperty("grid", out var gridEl) || !scrambleEl.TryGetProperty("seed", out var seedEl))
        {
            throw new InvalidOperationException(
                $"Unexpected scramble payload for page {url}: {Truncate(scrambleEl.GetRawText())}");
        }

        var grid = gridEl.GetInt32();
        var seed = seedEl.GetUInt32();

        using var source = Image.Load<Rgb24>(raw);
        using var descrambled = MangaDeniziDescrambler.Descramble(source, grid, seed);

        using var output = new MemoryStream();
        await descrambled.SaveAsJpegAsync(output, new JpegEncoder { Quality = 90 }, ct);
        return output.ToArray();
    }

    private async Task<byte[]> FetchImageBytesAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Referrer = new Uri($"{BaseUrl}/");
        using var response = await Client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private async Task<(JsonElement Manga, JsonResponse Response)> GetMangaAsync(string sourceSeriesId, CancellationToken ct)
    {
        var response = await GetJsonAsync($"api/v1/web/manga/{sourceSeriesId}", ct);
        var data = RequireProperty(response, "data");
        return (RequireProperty(data, response, "manga"), response);
    }

    private async Task<JsonResponse> GetJsonAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Referrer = ApiReferer;
        using var response = await Client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var url = request.RequestUri!;

        try
        {
            using var doc = JsonDocument.Parse(body);
            return new JsonResponse(doc.RootElement.Clone(), body, url);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"Unexpected response from {url}: {Truncate(body)}");
        }
    }

    private static JsonElement RequireProperty(JsonResponse response, string name)
    {
        if (!response.Root.TryGetProperty(name, out var value))
        {
            throw Unexpected(response);
        }

        return value;
    }

    private static JsonElement RequireProperty(JsonElement element, JsonResponse response, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            throw Unexpected(response);
        }

        return value;
    }

    private static InvalidOperationException Unexpected(JsonResponse response) =>
        new($"Unexpected response from {response.Url}: {Truncate(response.Body)}");

    private static string Truncate(string body) => body.Length <= 100 ? body : body[..100];

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static DateTime? ParseDate(string? raw) =>
        raw is not null && DateTime.TryParse(
            raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private readonly record struct JsonResponse(JsonElement Root, string Body, Uri Url);
}
