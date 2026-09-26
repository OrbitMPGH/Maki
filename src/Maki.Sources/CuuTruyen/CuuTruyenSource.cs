using System.Globalization;
using System.Text;
using System.Text.Json;
using Maki.Core.Http;
using Maki.Core.Parsing;
using Maki.Core.Sources;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Maki.Sources.CuuTruyen;

/// <summary>
/// Cứu Truyện: Vietnamese scanlation aggregator, JSON API under /api/v2. Cloudflare-fronted, so the
/// API side goes through <see cref="IHtmlFetcher"/> (direct with cached clearance first, FlareSolverr
/// on a miss); page bytes need a binary response, which the fetcher can't give back, so those go
/// through a named <see cref="IHttpClientFactory"/> client instead.
/// <para>
/// Covers and page images are served from <c>storage-ct.lrclib.net</c>/<c>storage-ct-riften.site</c>
/// in the API's own JSON, but neither host answers a request. The site's own JavaScript swaps them
/// for <c>storage-bravo.cuutruyen.net</c>/<c>storage-charlie.cuutruyen.net</c> before use, so every
/// URL from the API is rewritten the same way here.
/// </para>
/// <para>
/// Pages carry a base64+XOR "drm_data" blob that decodes to a row-shuffle map (<c>#v4|dy-h|dy-h|...</c>):
/// the true image is the fetched bytes with horizontal strips, consecutive from the top, moved to the
/// offsets the map names. A page with no drm_data is already in order and is left as a plain URL
/// request for the downloader.
/// </para>
/// </summary>
public class CuuTruyenSource(IHtmlFetcher fetcher, IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-cuutruyen";

    /// <summary>The key CuuTruyen's frontend XORs drm_data with, byte for byte (ASCII digits of pi).</summary>
    private static readonly byte[] DrmKey = "3141592653589793"u8.ToArray();

    private static readonly (string From, string To)[] HostRewrites =
    [
        ("storage-ct.lrclib.net", "storage-bravo.cuutruyen.net"),
        ("storage-ct-riften.site", "storage-charlie.cuutruyen.net")
    ];

    private HttpClient ImageClient => httpClientFactory.CreateClient(HttpClientName);

    public string Name => "cuutruyen";
    public string DisplayName => "Cứu Truyện";

    public string BaseUrl =>
        Environment.GetEnvironmentVariable("MAKI_SOURCE_CUUTRUYEN_BASEURL")?.TrimEnd('/') ?? "https://cuutruyen.net";

    public SourceCapabilities Capabilities => SourceCapabilities.NeedsFlareSolverr;
    public IReadOnlyList<string> SupportedLanguages => ["vi"];
    public IReadOnlyList<string> CoverHosts => ["cuutruyen.net"];

    public string? ResolveSeriesIdFromUrl(Uri url)
    {
        var tail = SourceUrl.PathTail(url, BaseUrl, "/mangas/", firstSegmentOnly: false);
        return tail is { Length: > 0 } && tail.All(char.IsAsciiDigit) ? tail : null;
    }

    // ── Search ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/api/v2/mangas/search?q={Uri.EscapeDataString(title)}&page=1&per_page=24";
        using var json = await FetchJsonAsync(url, ct);

        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<SourceSeriesResult>();
        foreach (var item in data.EnumerateArray())
        {
            var id = item.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture);
            var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
            var cover = item.TryGetProperty("cover_url", out var c) ? RewriteHost(c.GetString()) : null;

            results.Add(new SourceSeriesResult(
                id, string.IsNullOrEmpty(name) ? id : name, $"{BaseUrl}/mangas/{id}", cover));
        }

        return results;
    }

    // ── Series detail ─────────────────────────────────────────────────

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        using var json = await FetchJsonAsync($"{BaseUrl}/api/v2/mangas/{sourceSeriesId}", ct);
        var data = json.RootElement.GetProperty("data");

        var title = data.TryGetProperty("name", out var n) ? n.GetString() : null;
        var cover = data.TryGetProperty("cover_url", out var c) ? RewriteHost(c.GetString()) : null;

        return new SourceSeriesDetail(
            sourceSeriesId,
            string.IsNullOrEmpty(title) ? sourceSeriesId : title,
            $"{BaseUrl}/mangas/{sourceSeriesId}",
            cover,
            Description(data),
            Status(data));
    }

    /// <summary>Full_description is the site's own HTML; description is already plain text.</summary>
    private static string? Description(JsonElement data)
    {
        if (data.TryGetProperty("full_description", out var full)
            && full.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(full.GetString()))
        {
            var parser = new AngleSharp.Html.Parser.HtmlParser();
            var doc = parser.ParseDocument(full.GetString()!);
            var text = doc.Body?.TextContent.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }
        }

        return data.TryGetProperty("description", out var d) ? d.GetString() : null;
    }

    /// <summary>
    /// No dedicated status field. Keiyoushi's rule reads it off the tag names instead: a "hoàn
    /// thành" tag means Completed, "tạm ngưng" means Hiatus, and no such tag (most series, including
    /// One Piece) reads Ongoing.
    /// </summary>
    private static string Status(JsonElement data)
    {
        if (data.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tags.EnumerateArray())
            {
                var name = tag.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                var lower = name.ToLowerInvariant();
                if (lower.Contains("hoàn thành", StringComparison.Ordinal))
                {
                    return "Completed";
                }

                if (lower.Contains("tạm ngưng", StringComparison.Ordinal))
                {
                    return "Hiatus";
                }
            }
        }

        return "Ongoing";
    }

    // ── Chapters ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        using var json = await FetchJsonAsync($"{BaseUrl}/api/v2/mangas/{sourceSeriesId}/chapters", ct);

        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var chapters = new List<SourceChapter>();
        foreach (var entry in data.EnumerateArray())
        {
            var status = entry.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (!string.Equals(status, "processed", StringComparison.OrdinalIgnoreCase))
            {
                // Not yet uploaded; every row sampled was "processed" but the field exists for a reason.
                continue;
            }

            var chapter = ToChapter(sourceSeriesId, entry);
            if (chapter is not null)
            {
                chapters.Add(chapter);
            }
        }

        // The API lists newest first; Normalize both dedupes and re-sorts ascending.
        return SourceChapterList.Normalize(chapters);
    }

    private SourceChapter? ToChapter(string sourceSeriesId, JsonElement entry)
    {
        if (!entry.TryGetProperty("id", out var idEl))
        {
            return null;
        }

        var id = idEl.GetInt64().ToString(CultureInfo.InvariantCulture);
        var numberRaw = entry.TryGetProperty("number", out var num) ? num.GetString() : null;
        var parsed = ChapterNumberParser.Parse(numberRaw);
        var name = entry.TryGetProperty("name", out var nm) ? nm.GetString() : null;

        // A null Number ("Movie", "Crossover") is deduped by title downstream (ChapterIdentity), so
        // it must never carry a null Title too, or two different specials read as one chapter.
        var title = !string.IsNullOrEmpty(name) ? name : (parsed.Number is null ? numberRaw : null);

        DateTime? releaseDate = entry.TryGetProperty("created_at", out var created)
            && created.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                created.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.UtcDateTime
            : null;

        return new SourceChapter(
            Name,
            sourceSeriesId,
            id,
            numberRaw,
            parsed.Number,
            Volume: null,
            title,
            Language: "vi",
            releaseDate,
            Url: $"{BaseUrl}/mangas/{sourceSeriesId}/chapters/{id}");
    }

    // ── Page images ───────────────────────────────────────────────────

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        using var json = await FetchJsonAsync($"{BaseUrl}/api/v2/chapters/{chapter.SourceChapterId}", ct);
        var data = json.RootElement.GetProperty("data");

        if (!data.TryGetProperty("pages", out var pagesEl) || pagesEl.ValueKind != JsonValueKind.Array)
        {
            throw new ChapterLockedException($"CuuTruyen chapter {chapter.SourceChapterId} has no pages");
        }

        var entries = pagesEl.EnumerateArray()
            .OrderBy(p => p.TryGetProperty("order", out var o) ? o.GetInt32() : 0)
            .ToList();

        if (entries.Count == 0)
        {
            throw new ChapterLockedException($"CuuTruyen chapter {chapter.SourceChapterId} has no pages");
        }

        var headers = new Dictionary<string, string> { ["Referer"] = "https://cuutruyen.net/" };
        var pages = new List<PageRequest>(entries.Count);

        foreach (var page in entries)
        {
            var imageUrl = page.TryGetProperty("image_url", out var iu) ? iu.GetString() : null;
            if (string.IsNullOrEmpty(imageUrl))
            {
                continue;
            }

            var url = RewriteHost(imageUrl)!;
            var drmData = page.TryGetProperty("drm_data", out var dd) && dd.ValueKind == JsonValueKind.String
                ? dd.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(drmData))
            {
                pages.Add(new PageRequest(url, headers));
                continue;
            }

            var bytes = await FetchBytesAsync(url, headers, ct);
            var unscrambled = await UnscrambleAsync(bytes, drmData, ct);
            pages.Add(new PageRequest(url, headers, Data: unscrambled));
        }

        return new ChapterPages(pages);
    }

    private async Task<byte[]> FetchBytesAsync(
        string url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (key, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }

        using var response = await ImageClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>
    /// Reverses the row-shuffle DRM: <paramref name="drmData"/> is newline-noise plus base64, XORed
    /// with <see cref="DrmKey"/> to a "#v4|dy-h|dy-h|..." map. The source image's horizontal strips,
    /// consecutive from the top, each move to the <c>dy</c> the map names for it.
    /// </summary>
    internal static async Task<byte[]> UnscrambleAsync(byte[] imageBytes, string drmData, CancellationToken ct = default)
    {
        var base64 = drmData.Replace("\n", string.Empty, StringComparison.Ordinal).Replace("\r", string.Empty, StringComparison.Ordinal);
        var encrypted = Convert.FromBase64String(base64);
        var decrypted = new byte[encrypted.Length];
        for (var i = 0; i < encrypted.Length; i++)
        {
            decrypted[i] = (byte)(encrypted[i] ^ DrmKey[i % DrmKey.Length]);
        }

        var map = Encoding.UTF8.GetString(decrypted);
        if (!map.StartsWith("#v4|", StringComparison.Ordinal))
        {
            throw new InvalidDataException("unsupported CuuTruyen DRM");
        }

        var strips = map.Split('|')
            .Skip(1)
            .Where(t => t.Length > 0)
            .Select(t =>
            {
                var dash = t.IndexOf('-');
                return (
                    Dy: int.Parse(t[..dash], CultureInfo.InvariantCulture),
                    H: int.Parse(t[(dash + 1)..], CultureInfo.InvariantCulture));
            })
            .ToList();

        using var source = Image.Load<Rgba32>(imageBytes);
        using var destination = new Image<Rgba32>(source.Width, source.Height);

        var sourceY = 0;
        destination.Mutate(ctx =>
        {
            foreach (var (dy, h) in strips)
            {
                ctx.DrawImage(source, new Point(0, dy), new Rectangle(0, sourceY, source.Width, h), 1f);
                sourceY += h;
            }
        });

        IImageEncoder encoder = source.Metadata.DecodedImageFormat is { } format
            ? source.Configuration.ImageFormatsManager.GetEncoder(format)
            : new JpegEncoder { Quality = 90 };

        using var buffer = new MemoryStream();
        await destination.SaveAsync(buffer, encoder, ct);
        return buffer.ToArray();
    }

    // ── Plumbing ──────────────────────────────────────────────────────

    private async Task<JsonDocument> FetchJsonAsync(string url, CancellationToken ct)
    {
        var body = await fetcher.GetHtmlAsync(url, ct);
        var unwrapped = await CuuTruyenPreUnwrap.UnwrapAsync(body, url, ct);
        return JsonDocument.Parse(unwrapped);
    }

    private static string? RewriteHost(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return url;
        }

        foreach (var (from, to) in HostRewrites)
        {
            if (url.Contains(from, StringComparison.OrdinalIgnoreCase))
            {
                return url.Replace(from, to, StringComparison.OrdinalIgnoreCase);
            }
        }

        return url;
    }
}
