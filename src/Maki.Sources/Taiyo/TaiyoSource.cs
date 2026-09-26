using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Maki.Core.Parsing;
using Maki.Core.Sources;

namespace Maki.Sources.Taiyo;

/// <summary>
/// Taiyo scraper. taiyo.moe is a Next.js app backed by a tRPC v11 API for series,
/// chapters and pages, and by a Meilisearch instance for search. All three hosts
/// (taiyo.moe, meilisearch.taiyo.moe, cdn.taiyo.moe) are used with absolute URLs on
/// one client with no BaseAddress. Meilisearch needs a public Bearer key that is a
/// build-time constant baked into the site's Next.js bundle; it is discovered on
/// first search and cached, and rediscovered once if it stops working.
/// </summary>
public partial class TaiyoSource(IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-taiyo";

    private const string CdnUrl = "https://cdn.taiyo.moe";
    private const string DefaultMeilisearchUrl = "https://meilisearch.taiyo.moe";

    private static readonly HtmlParser Parser = new();

    private readonly SemaphoreSlim _meilisearchLock = new(1, 1);
    private volatile MeilisearchConfig? _meilisearchConfig;

    public string Name => "taiyo";
    public string DisplayName => "Taiyō";
    public string BaseUrl => "https://taiyo.moe";
    public SourceCapabilities Capabilities => SourceCapabilities.None;
    public IReadOnlyList<string> SupportedLanguages => ["pt-br"];
    public IReadOnlyList<string> CoverHosts => [];

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    public string? ResolveSeriesIdFromUrl(Uri url)
    {
        var tail = SourceUrl.PathTail(url, BaseUrl, "/media/", firstSegmentOnly: true);
        return tail is not null && Guid.TryParse(tail, out _) ? tail : null;
    }

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var config = await GetMeilisearchConfigAsync(ct);
        var (url, response) = await PostMultiSearchAsync(config, title, ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            response.Dispose();
            await InvalidateMeilisearchConfigAsync(config, ct);
            config = await GetMeilisearchConfigAsync(ct);
            (url, response) = await PostMultiSearchAsync(config, title, ct);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Meilisearch search at {url} failed with {(int)response.StatusCode}: {Truncate(body)}");
            }

            return ParseSearchResults(url, body);
        }
    }

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var json = await TrpcGetAsync("medias.getById", sourceSeriesId, ct);

        var title = json.TryGetProperty("mainTitle", out var titleEl) ? titleEl.GetString() : null;
        var cover = CoverUrl(sourceSeriesId, json);
        var description = json.TryGetProperty("synopsis", out var synEl) ? synEl.GetString() : null;
        var status = json.TryGetProperty("status", out var statusEl) ? statusEl.GetString() : null;

        return new SourceSeriesDetail(
            sourceSeriesId,
            string.IsNullOrWhiteSpace(title) ? sourceSeriesId : title,
            $"{BaseUrl}/media/{sourceSeriesId}",
            cover,
            description,
            status);
    }

    /// <summary>
    /// Trackers come off the same medias.getById payload as GetSeriesAsync, not a separate
    /// endpoint, so a candidate the caller is about to fetch series details for anyway costs
    /// nothing extra here beyond the one call it would make regardless.
    /// </summary>
    public Task<IReadOnlyDictionary<string, string>?> GetExternalIdsAsync(
        string sourceSeriesId, CancellationToken ct = default) =>
        GetExternalIdsCoreAsync(sourceSeriesId, ct);

    private async Task<IReadOnlyDictionary<string, string>?> GetExternalIdsCoreAsync(
        string sourceSeriesId, CancellationToken ct)
    {
        var json = await TrpcGetAsync("medias.getById", sourceSeriesId, ct);
        if (!json.TryGetProperty("trackers", out var trackersEl) || trackersEl.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tracker in trackersEl.EnumerateArray())
        {
            var trackerName = tracker.TryGetProperty("tracker", out var nameEl) ? nameEl.GetString() : null;
            var externalId = tracker.TryGetProperty("externalId", out var idEl) ? idEl.GetString() : null;
            var service = trackerName switch
            {
                "ANILIST" => ExternalIdService.AniList,
                "MYANIMELIST" => ExternalIdService.Mal,
                "MANGAUPDATES" => ExternalIdService.MangaUpdates,
                "KITSU" => ExternalIdService.Kitsu,
                "MANGADEX" => ExternalIdService.MangaDex,
                _ => null
            };

            if (service is not null)
            {
                SourceExternalIds.Set(map, service, externalId);
            }
        }

        return map.Count == 0 ? null : map;
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var chapters = new List<SourceChapter>();
        var page = 1;
        int totalPages;

        do
        {
            var arg = new Dictionary<string, object> { ["mediaId"] = sourceSeriesId, ["page"] = page, ["perPage"] = 50 };
            var json = await TrpcGetAsync("chapters.getByMediaId", arg, ct);

            totalPages = json.TryGetProperty("totalPages", out var totalEl) && totalEl.ValueKind == JsonValueKind.Number
                ? totalEl.GetInt32()
                : page;

            if (json.TryGetProperty("chapters", out var rows) && rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    var chapter = ParseChapter(sourceSeriesId, row);
                    if (chapter is not null)
                    {
                        chapters.Add(chapter);
                    }
                }
            }

            page++;
        } while (page <= totalPages);

        return SourceChapterList.Normalize(chapters);
    }

    private SourceChapter? ParseChapter(string sourceSeriesId, JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object ||
            !row.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String ||
            !row.TryGetProperty("number", out var numberEl))
        {
            return null;
        }

        var chapterId = idEl.GetString();
        if (string.IsNullOrEmpty(chapterId))
        {
            return null;
        }

        var rawNumber = numberEl.ValueKind == JsonValueKind.Number ? numberEl.GetRawText() : numberEl.GetString();
        var parsed = ChapterNumberParser.Parse(rawNumber);

        var volume = row.TryGetProperty("volume", out var volumeEl) && volumeEl.ValueKind == JsonValueKind.Number
            ? volumeEl.GetInt32()
            : (int?)null;

        var chapterTitle = row.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;

        DateTime? releaseDate = row.TryGetProperty("createdAt", out var createdEl) &&
                                createdEl.ValueKind == JsonValueKind.String &&
                                DateTime.TryParse(
                                    createdEl.GetString(), CultureInfo.InvariantCulture,
                                    DateTimeStyles.AdjustToUniversal, out var parsedDate)
            ? parsedDate
            : null;

        return new SourceChapter(
            Name,
            sourceSeriesId,
            chapterId,
            rawNumber,
            parsed.Number,
            volume,
            Title: string.IsNullOrWhiteSpace(chapterTitle) ? null : chapterTitle,
            Language: "pt-br",
            ReleaseDate: releaseDate,
            Url: $"{BaseUrl}/chapter/{chapterId}/1");
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var json = await TrpcGetAsync("chapters.getById", chapter.SourceChapterId, ct);

        if (!json.TryGetProperty("pages", out var pagesEl) ||
            pagesEl.ValueKind != JsonValueKind.Array ||
            pagesEl.GetArrayLength() == 0)
        {
            throw new ChapterLockedException($"Chapter {chapter.SourceChapterId} has no pages on {DisplayName}");
        }

        var mediaId = json.TryGetProperty("media", out var mediaEl) &&
                      mediaEl.TryGetProperty("id", out var mediaIdEl) &&
                      mediaIdEl.ValueKind == JsonValueKind.String
            ? mediaIdEl.GetString()!
            : chapter.SourceSeriesId;

        var headers = new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" };
        var pages = new List<PageRequest>();
        foreach (var page in pagesEl.EnumerateArray())
        {
            var pageId = page.TryGetProperty("id", out var pageIdEl) ? pageIdEl.GetString() : null;
            var extension = page.TryGetProperty("extension", out var extEl) ? extEl.GetString() : null;
            if (string.IsNullOrEmpty(pageId) || string.IsNullOrEmpty(extension))
            {
                continue;
            }

            pages.Add(new PageRequest(
                $"{CdnUrl}/medias/{mediaId}/chapters/{chapter.SourceChapterId}/{pageId}.{extension}", headers));
        }

        return new ChapterPages(pages);
    }

    private string? CoverUrl(string sourceSeriesId, JsonElement json) =>
        json.TryGetProperty("coverId", out var coverEl) && coverEl.ValueKind == JsonValueKind.String
            ? $"{CdnUrl}/medias/{sourceSeriesId}/covers/{coverEl.GetString()}.jpg"
            : null;

    private static IReadOnlyList<SourceSeriesResult> ParseSearchResults(string url, string body)
    {
        var root = ParseJson(url, body);
        using (root.Document)
        {
            if (!root.Element.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array ||
                results.GetArrayLength() == 0)
            {
                return [];
            }

            var hitsHolder = results[0];
            if (!hitsHolder.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var list = new List<SourceSeriesResult>();
            foreach (var hit in hits.EnumerateArray())
            {
                var id = hit.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                var title = PickTitle(hit, id);
                var cover = hit.TryGetProperty("mainCoverId", out var coverEl) && coverEl.ValueKind == JsonValueKind.String
                    ? $"{CdnUrl}/medias/{id}/covers/{coverEl.GetString()}.jpg"
                    : null;
                var synopsis = hit.TryGetProperty("synopsis", out var synEl) ? synEl.GetString() : null;

                list.Add(new SourceSeriesResult(id, title, $"https://taiyo.moe/media/{id}", cover, synopsis));
            }

            return list;
        }
    }

    /// <summary>
    /// The main title in English, falling back through main title in any language, lowest-priority
    /// English title, romanized Japanese, then simply the first title on the hit.
    /// </summary>
    private static string PickTitle(JsonElement hit, string fallbackId)
    {
        if (!hit.TryGetProperty("titles", out var titlesEl) || titlesEl.ValueKind != JsonValueKind.Array)
        {
            return fallbackId;
        }

        var titles = titlesEl.EnumerateArray().ToList();
        if (titles.Count == 0)
        {
            return fallbackId;
        }

        return titles.Where(t => IsMainTitle(t) && Language(t) == "en").Select(TitleText).FirstOrDefault(t => t is not null)
            ?? titles.Where(IsMainTitle).Select(TitleText).FirstOrDefault(t => t is not null)
            ?? titles.Where(t => Language(t) == "en").OrderBy(Priority).Select(TitleText).FirstOrDefault(t => t is not null)
            ?? titles.Where(t => Language(t) == "ja_ro").Select(TitleText).FirstOrDefault(t => t is not null)
            ?? TitleText(titles[0])
            ?? fallbackId;
    }

    private static bool IsMainTitle(JsonElement title) =>
        title.TryGetProperty("isMainTitle", out var el) && el.ValueKind == JsonValueKind.True;

    private static string? Language(JsonElement title) =>
        title.TryGetProperty("language", out var el) ? el.GetString() : null;

    private static int Priority(JsonElement title) =>
        title.TryGetProperty("priority", out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : int.MaxValue;

    private static string? TitleText(JsonElement title) =>
        title.TryGetProperty("title", out var el) ? el.GetString() : null;

    private async Task<(string Url, HttpResponseMessage Response)> PostMultiSearchAsync(
        MeilisearchConfig config, string title, CancellationToken ct)
    {
        var url = $"{config.BaseUrl}/multi-search";
        var body = JsonSerializer.Serialize(new
        {
            queries = new[]
            {
                new
                {
                    indexUid = "medias",
                    q = title,
                    filter = new[] { "deletedAt IS NULL" },
                    limit = 21,
                    offset = 0
                }
            }
        });

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.Key);

        return (url, await Client.SendAsync(request, ct));
    }

    private async Task<MeilisearchConfig> GetMeilisearchConfigAsync(CancellationToken ct)
    {
        if (_meilisearchConfig is { } cached)
        {
            return cached;
        }

        await _meilisearchLock.WaitAsync(ct);
        try
        {
            if (_meilisearchConfig is { } cachedAfterWait)
            {
                return cachedAfterWait;
            }

            var config = await DiscoverMeilisearchConfigAsync(ct);
            _meilisearchConfig = config;
            return config;
        }
        finally
        {
            _meilisearchLock.Release();
        }
    }

    /// <summary>
    /// Clears the cached config only if it still holds the exact instance a failed request used.
    /// Guards against a race where another caller already rediscovered a fresh key between our
    /// request failing and this call: without the identity check we could throw away a key that
    /// works, forcing an unnecessary rediscovery for everyone.
    /// </summary>
    private async Task InvalidateMeilisearchConfigAsync(MeilisearchConfig used, CancellationToken ct)
    {
        await _meilisearchLock.WaitAsync(ct);
        try
        {
            if (ReferenceEquals(_meilisearchConfig, used))
            {
                _meilisearchConfig = null;
            }
        }
        finally
        {
            _meilisearchLock.Release();
        }
    }

    /// <summary>
    /// The Meilisearch Bearer key is a public build-time constant baked into the site's Next.js
    /// bundle, not a secret endpoint. It is looked for first in the app/layout-*.js chunk (where it
    /// lived when this was written), then in every other chunk the home page references, the same
    /// order Keiyoushi's Taiyo extension uses.
    /// </summary>
    private async Task<MeilisearchConfig> DiscoverMeilisearchConfigAsync(CancellationToken ct)
    {
        var home = await Client.GetStringAsync($"{BaseUrl}/", ct);
        var doc = await Parser.ParseDocumentAsync(home, ct);

        var scripts = doc.QuerySelectorAll("script[src]")
            .Select(s => s.GetAttribute("src"))
            .Where(src => !string.IsNullOrEmpty(src))
            .Select(src => src!)
            .ToList();

        var candidates = scripts.Where(s => s.Contains("/app/layout-", StringComparison.Ordinal))
            .Concat(scripts.Where(s => s.Contains("/_next/static/chunks/", StringComparison.Ordinal)))
            .Distinct();

        foreach (var src in candidates)
        {
            var scriptUrl = src.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? src : $"{BaseUrl}{src}";

            string js;
            try
            {
                js = await Client.GetStringAsync(scriptUrl, ct);
            }
            catch (HttpRequestException)
            {
                continue;
            }

            var keyMatch = MeilisearchKeyPattern().Match(js);
            if (!keyMatch.Success)
            {
                continue;
            }

            var urlMatch = MeilisearchUrlPattern().Match(js);
            return new MeilisearchConfig(
                keyMatch.Groups[1].Value, urlMatch.Success ? urlMatch.Groups[1].Value : DefaultMeilisearchUrl);
        }

        throw new InvalidOperationException($"Could not find the Meilisearch public key in {BaseUrl}'s bundle");
    }

    private async Task<JsonElement> TrpcGetAsync(string procedure, object arg, CancellationToken ct)
    {
        var inputJson = JsonSerializer.Serialize(new Dictionary<string, object> { ["0"] = new { json = arg } });
        var url = $"{BaseUrl}/api/trpc/{procedure}?batch=1&input={Uri.EscapeDataString(inputJson)}";
        var body = await Client.GetStringAsync(url, ct);
        return ParseTrpcResult(procedure, url, body);
    }

    private static JsonElement ParseTrpcResult(string procedure, string url, string body)
    {
        var root = ParseJson(url, body);
        using (root.Document)
        {
            var batch = root.Element;
            if (batch.ValueKind != JsonValueKind.Array || batch.GetArrayLength() == 0)
            {
                throw new InvalidOperationException($"Unexpected tRPC response shape from {url}: {Truncate(body)}");
            }

            var entry = batch[0];
            if (entry.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("data", out var data) && data.TryGetProperty("code", out var codeEl)
                    ? codeEl.GetString()
                    : "unknown";
                throw new InvalidOperationException($"tRPC {procedure} at {url} returned error {code}");
            }

            if (!entry.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("data", out var data2) ||
                !data2.TryGetProperty("json", out var json))
            {
                throw new InvalidOperationException($"Unexpected tRPC response shape from {url}: {Truncate(body)}");
            }

            return json.Clone();
        }
    }

    private static (JsonDocument Document, JsonElement Element) ParseJson(string url, string body)
    {
        try
        {
            var document = JsonDocument.Parse(body);
            return (document, document.RootElement);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"Unexpected response from {url}: {Truncate(body)}");
        }
    }

    private static string Truncate(string body) => body.Length <= 100 ? body : body[..100];

    [GeneratedRegex("NEXT_PUBLIC_MEILISEARCH_PUBLIC_KEY:\\s*\"([^\"]+)\"")]
    private static partial Regex MeilisearchKeyPattern();

    [GeneratedRegex("NEXT_PUBLIC_MEILISEARCH_URL:\\s*\"([^\"]+)\"")]
    private static partial Regex MeilisearchUrlPattern();

    private sealed record MeilisearchConfig(string Key, string BaseUrl);
}
