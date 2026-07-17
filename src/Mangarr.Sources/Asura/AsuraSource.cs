using System.Text.Json;
using Mangarr.Core.Parsing;
using Mangarr.Core.Sources;

namespace Mangarr.Sources.Asura;

/// <summary>
/// Asura Scans source. Asura is an Astro site backed by a JSON API
/// (api.asurascans.com); we use the API for search, chapter list, and page images.
/// It is manhwa/manhua-focused (Korean/Chinese webtoons), not Japanese manga.
/// The series id is the hashed public slug ("{slug}-{hash}"); the chapter id packs
/// the series slug and raw chapter number as "{slug}|{number}" so GetPagesAsync can
/// rebuild the per-chapter endpoint. Premium chapters whose early-access window
/// hasn't opened serve no pages, so they're skipped to avoid empty grabs.
/// </summary>
public class AsuraSource(IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-asura";

    public string Name => "asura";
    public string DisplayName => "Asura Scans";
    public string BaseUrl => "https://asurascans.com";
    public SourceCapabilities Capabilities => SourceCapabilities.None;

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    public string? ResolveSeriesIdFromUrl(Uri url) =>
        // https://asurascans.com/comics/{slug}-{hash}
        SourceUrl.PathTail(url, BaseUrl, "/comics/", firstSegmentOnly: true);

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var root = await GetAsync($"api/series?search={Uri.EscapeDataString(title)}", ct);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<SourceSeriesResult>();
        foreach (var item in data.EnumerateArray())
        {
            var slug = PublicSlug(item);
            if (string.IsNullOrEmpty(slug))
            {
                continue;
            }

            var name = item.TryGetProperty("title", out var t) ? t.GetString() : null;
            var cover = item.TryGetProperty("cover", out var c) ? c.GetString()
                : item.TryGetProperty("thumbnail", out var th) ? th.GetString() : null;
            results.Add(new SourceSeriesResult(slug, name ?? "Unknown", $"{BaseUrl}/comics/{slug}", cover));
        }

        return results;
    }

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var root = await GetAsync($"api/series/{sourceSeriesId}", ct);
        var data = root.TryGetProperty("data", out var d) ? d : root;

        var title = data.TryGetProperty("title", out var t) ? t.GetString() ?? sourceSeriesId : sourceSeriesId;
        var cover = data.TryGetProperty("cover", out var c) ? c.GetString() : null;
        var description = data.TryGetProperty("description", out var desc) ? desc.GetString()
            : data.TryGetProperty("summary", out var summ) ? summ.GetString() : null;
        var status = data.TryGetProperty("status", out var s) ? s.GetString() : null;

        return new SourceSeriesDetail(sourceSeriesId, title, $"{BaseUrl}/comics/{sourceSeriesId}", cover, description, status);
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var root = await GetAsync($"api/series/{sourceSeriesId}/chapters", ct);
        var rows = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? d
            : root.ValueKind == JsonValueKind.Array ? root
            : default;
        if (rows.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var chapters = new List<SourceChapter>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("number", out var numberEl))
            {
                continue;
            }

            // Premium chapters are locked until their early-access window passes; they
            // serve no pages, so skip them to avoid empty grabs.
            var premium = row.TryGetProperty("is_premium", out var p) && p.ValueKind == JsonValueKind.True;
            var pageCount = row.TryGetProperty("page_count", out var pc) && pc.ValueKind == JsonValueKind.Number ? pc.GetInt32() : 0;
            if (premium && pageCount == 0)
            {
                continue;
            }

            var rawNumber = numberEl.ValueKind == JsonValueKind.Number ? numberEl.GetRawText() : numberEl.GetString();
            if (string.IsNullOrEmpty(rawNumber))
            {
                continue;
            }

            var parsed = ChapterNumberParser.Parse(rawNumber);
            if (parsed.Number is null)
            {
                continue;
            }

            var chapterTitle = row.TryGetProperty("title", out var ct2) ? ct2.GetString() : null;

            chapters.Add(new SourceChapter(
                Name,
                sourceSeriesId,
                // GetPagesAsync needs both the series slug and the raw chapter number
                $"{sourceSeriesId}|{rawNumber}",
                rawNumber,
                parsed.Number,
                parsed.Volume,
                Title: string.IsNullOrWhiteSpace(chapterTitle) ? null : chapterTitle,
                Language: "en",
                ReleaseDate: null,
                Url: $"{BaseUrl}/comics/{sourceSeriesId}/chapter/{rawNumber}"));
        }

        return SourceChapterList.Normalize(chapters);
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        // SourceChapterId is "{series_slug}|{chapter_number}"
        var separator = chapter.SourceChapterId.LastIndexOf('|');
        var seriesSlug = separator >= 0 ? chapter.SourceChapterId[..separator] : chapter.SourceChapterId;
        var number = separator >= 0 ? chapter.SourceChapterId[(separator + 1)..] : chapter.SourceChapterId;

        var root = await GetAsync($"api/series/{seriesSlug}/chapters/{number}", ct);
        var pages = new List<PageRequest>();
        if (root.TryGetProperty("data", out var data) &&
            data.TryGetProperty("chapter", out var ch) &&
            ch.TryGetProperty("pages", out var pageArray) &&
            pageArray.ValueKind == JsonValueKind.Array)
        {
            var headers = new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" };
            foreach (var page in pageArray.EnumerateArray())
            {
                var url = page.ValueKind == JsonValueKind.String ? page.GetString()
                    : page.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (!string.IsNullOrEmpty(url))
                {
                    pages.Add(new PageRequest(url, headers));
                }
            }
        }

        return new ChapterPages(pages);
    }

    private static string PublicSlug(JsonElement item)
    {
        // public_url is "/comics/{slug}-{hash}"; the hashed slug is what the chapter
        // endpoints expect. Fall back to the bare slug.
        var url = item.TryGetProperty("public_url", out var pu) ? pu.GetString() : null;
        if (url is not null && url.StartsWith("/comics/", StringComparison.Ordinal))
        {
            return url["/comics/".Length..].Trim('/');
        }

        return item.TryGetProperty("slug", out var s) ? s.GetString() ?? string.Empty : string.Empty;
    }

    private async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        var body = await Client.GetStringAsync(path, ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }
}
