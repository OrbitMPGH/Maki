using System.Text.Json;
using Maki.Core.Parsing;
using Maki.Core.Sources;

namespace Maki.Sources.ManhwaWeb;

/// <summary>
/// ManhwaWeb source. The SPA (manhwaweb.com) is Cloudflare-fronted, but everything the source
/// needs comes from its separate JSON API host, which answers plain HTTP with no challenge.
/// <para>
/// A series carries two ids: <c>_id</c> and <c>real_id</c>. They usually match, but not always
/// (a renamed series keeps its original <c>_id</c>). Chapter links in the series JSON are built
/// on <c>_id</c>, while the page endpoint only accepts the <c>real_id</c> form, so
/// <see cref="ListChaptersAsync"/> rewrites every chapter's id and URL to the <c>real_id</c> form
/// before handing it out, and <see cref="GetPagesAsync"/> never has to know the difference.
/// </para>
/// <para>
/// The catalog also lists prose novels (<c>_tipo == "novela"</c>), which are dropped from search.
/// </para>
/// </summary>
public class ManhwaWebSource(IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-manhwaweb";

    /// <summary>
    /// Confirmed 2026-09-26 in the SPA bundle. The backend is a Railway "production" deployment
    /// and can rotate without notice; grep the bundle for railway.app if this stops answering.
    /// </summary>
    public static string ApiUrl =>
        Environment.GetEnvironmentVariable("MAKI_SOURCE_MANHWAWEB_APIURL")?.TrimEnd('/')
        ?? "https://manhwawebbackend-production.up.railway.app";

    public string Name => "manhwaweb";
    public string DisplayName => "ManhwaWeb";
    public string BaseUrl => "https://manhwaweb.com";
    public SourceCapabilities Capabilities => SourceCapabilities.None;
    public SourceContent Content => SourceContent.Manhwa;
    public IReadOnlyList<string> SupportedLanguages => ["es"];
    public IReadOnlyList<string> CoverHosts => ["img1mw.xyz", "img2mw.xyz", "imageshack.com"];

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    public string? ResolveSeriesIdFromUrl(Uri url) =>
        SourceUrl.PathTail(url, BaseUrl, "/manhwa/", firstSegmentOnly: true);

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var results = new List<SourceSeriesResult>();

        // page is 0-based. A title query almost always fits on page 0; walking further is a
        // safety net, capped so a very generic query can't turn into an unbounded crawl.
        for (var page = 0; page < 3; page++)
        {
            var root = await GetAsync($"manhwa/library?buscar={Uri.EscapeDataString(title)}&page={page}", ct);
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("_tipo", out var tipoEl) &&
                    string.Equals(tipoEl.GetString(), "novela", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var realId = item.TryGetProperty("real_id", out var r) ? r.GetString() : null;
                if (string.IsNullOrEmpty(realId))
                {
                    continue;
                }

                var cover = item.TryGetProperty("_imagen", out var im) ? im.GetString() : null;
                results.Add(new SourceSeriesResult(realId, TitleOf(item, realId), $"{BaseUrl}/manhwa/{realId}", cover));
            }

            var hasNext = root.TryGetProperty("next", out var nextEl) && nextEl.ValueKind == JsonValueKind.True;
            if (!hasNext)
            {
                break;
            }
        }

        return results;
    }

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var root = await GetAsync($"manhwa/see/{sourceSeriesId}", ct);
        var realId = root.TryGetProperty("real_id", out var r) ? r.GetString() : null;
        var id = string.IsNullOrEmpty(realId) ? sourceSeriesId : realId;

        var cover = root.TryGetProperty("_imagen", out var im) ? im.GetString() : null;
        var synopsis = root.TryGetProperty("_sinopsis", out var s) ? s.GetString() : null;
        var status = root.TryGetProperty("_status", out var st) ? st.GetString() : null;

        return new SourceSeriesDetail(id, TitleOf(root, id), $"{BaseUrl}/manhwa/{id}", cover, synopsis, status);
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var root = await GetAsync($"manhwa/see/{sourceSeriesId}", ct);
        var realId = root.TryGetProperty("real_id", out var r) ? r.GetString() : null;
        realId = string.IsNullOrEmpty(realId) ? sourceSeriesId : realId;
        var internalId = root.TryGetProperty("_id", out var idEl) ? idEl.GetString() : null;
        internalId = string.IsNullOrEmpty(internalId) ? realId : internalId;

        if (!root.TryGetProperty("chapters", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var chapters = new List<SourceChapter>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("chapter", out var chapterEl))
            {
                continue;
            }

            var (link, create) = TranslatedLinkAndTimestamp(row);
            if (link is null || create is null)
            {
                continue;
            }

            var rawNumber = chapterEl.ValueKind == JsonValueKind.Number ? chapterEl.GetRawText() : chapterEl.GetString();
            if (string.IsNullOrEmpty(rawNumber))
            {
                continue;
            }

            var (chapterId, url) = RebuildOnRealId(link, internalId, realId);
            var parsed = ChapterNumberParser.Parse(rawNumber);

            chapters.Add(new SourceChapter(
                Name,
                realId,
                chapterId,
                rawNumber,
                parsed.Number,
                Volume: null,
                // An unparseable number has nothing else to identify it by, so the raw label
                // keeps it distinct from every other null-number chapter instead of collapsing
                // them all into one row.
                Title: parsed.Number is null ? rawNumber : null,
                Language: "es",
                ReleaseDate: DateTimeOffset.FromUnixTimeMilliseconds(create.Value).UtcDateTime,
                Url: url));
        }

        return SourceChapterList.Normalize(chapters);
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        // The page endpoint only accepts a chapterSlug built on real_id; a slug still carrying the
        // old _id answers the plain-text body "errorrgaarotosi" instead of JSON.
        var body = await Client.GetStringAsync($"chapters/see/{chapter.SourceChapterId}", ct);
        if (!body.TrimStart().StartsWith('{'))
        {
            throw new InvalidOperationException(
                $"ManhwaWeb pages endpoint returned a non-JSON response for chapter {chapter.SourceChapterId}: {body}");
        }

        using var doc = JsonDocument.Parse(body);
        var pages = new List<PageRequest>();
        if (doc.RootElement.TryGetProperty("chapter", out var chapterEl) &&
            chapterEl.TryGetProperty("img", out var imgArray) &&
            imgArray.ValueKind == JsonValueKind.Array)
        {
            var headers = new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" };
            foreach (var img in imgArray.EnumerateArray())
            {
                var url = img.ValueKind == JsonValueKind.String ? img.GetString() : null;
                if (!string.IsNullOrEmpty(url) && url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    pages.Add(new PageRequest(url, headers));
                }
            }
        }

        // No paid chapters on this site, so an empty page list here means something is actually
        // wrong (not yet uploaded, or the "roto" flag marking it broken) rather than a locked
        // chapter that will open later on its own. The queue should still retry it quietly
        // instead of writing an empty CBZ or failing the chapter permanently.
        if (pages.Count == 0)
        {
            var roto = doc.RootElement.TryGetProperty("roto", out var rotoEl) && rotoEl.ValueKind == JsonValueKind.String
                ? rotoEl.GetString()
                : null;
            var reason = !string.IsNullOrEmpty(roto) && !string.Equals(roto, "no", StringComparison.OrdinalIgnoreCase)
                ? $"roto={roto}"
                : "no pages in the response";
            throw new ChapterLockedException($"ManhwaWeb chapter {chapter.SourceChapterId} has no pages ({reason})");
        }

        return new ChapterPages(pages);
    }

    private static string TitleOf(JsonElement element, string fallback)
    {
        var nameEsp = element.TryGetProperty("name_esp", out var ne) ? ne.GetString() : null;
        if (!string.IsNullOrWhiteSpace(nameEsp))
        {
            return nameEsp;
        }

        var theRealName = element.TryGetProperty("the_real_name", out var tr) ? tr.GetString() : null;
        return string.IsNullOrWhiteSpace(theRealName) ? fallback : theRealName;
    }

    /// <summary>
    /// The translated link/timestamp for a chapters[] row, falling back to its first version.
    /// A row carrying only link_raw (the untranslated original) has neither and is dropped by
    /// the caller, since the site never says what language that raw text is in.
    /// </summary>
    private static (string? Link, long? Create) TranslatedLinkAndTimestamp(JsonElement row)
    {
        var link = row.TryGetProperty("link", out var linkEl) && linkEl.ValueKind == JsonValueKind.String
            ? linkEl.GetString()
            : null;
        long? create = row.TryGetProperty("create", out var createEl) && createEl.ValueKind == JsonValueKind.Number
            ? createEl.GetInt64()
            : null;

        if (link is not null && create is not null)
        {
            return (link, create);
        }

        if (row.TryGetProperty("versions", out var versions) &&
            versions.ValueKind == JsonValueKind.Array &&
            versions.GetArrayLength() > 0)
        {
            var first = versions[0];
            link ??= first.TryGetProperty("link", out var vLinkEl) && vLinkEl.ValueKind == JsonValueKind.String
                ? vLinkEl.GetString()
                : null;
            create ??= first.TryGetProperty("create", out var vCreateEl) && vCreateEl.ValueKind == JsonValueKind.Number
                ? vCreateEl.GetInt64()
                : null;
        }

        return (link, create);
    }

    /// <summary>
    /// Rewrites a chapter link's last path segment (built on <paramref name="internalId"/>, the
    /// series' <c>_id</c>) to use <paramref name="realId"/> instead, since that is the only form
    /// the page endpoint accepts.
    /// </summary>
    private static (string ChapterId, string Url) RebuildOnRealId(string link, string internalId, string realId)
    {
        var lastSlash = link.LastIndexOf('/');
        var segment = lastSlash >= 0 ? link[(lastSlash + 1)..] : link;

        if (!string.Equals(internalId, realId, StringComparison.Ordinal) &&
            segment.StartsWith(internalId, StringComparison.Ordinal))
        {
            segment = realId + segment[internalId.Length..];
            link = lastSlash >= 0 ? string.Concat(link.AsSpan(0, lastSlash + 1), segment) : segment;
        }

        return (segment, link);
    }

    private async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        var body = await Client.GetStringAsync(path, ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }
}
