using System.Globalization;
using System.Text.Json;
using Maki.Core.Parsing;
using Maki.Core.Sources;

namespace Maki.Sources.Shinigami;

/// <summary>
/// Shinigami source, Indonesian translations of manhwa/manhua/manga. The website
/// (numbered subdomain, e.g. 11.shinigami.asia) is Cloudflare-challenged, but its JSON API at
/// api.shngm.io answers plain HTTP with no challenge, so this source never touches the website.
/// Every response is wrapped in <c>{ retcode, message, meta, data }</c>; a non-zero retcode
/// is treated as an error. The website's numbered subdomain rotates, so <see cref="BaseUrl"/>
/// (used only for links and URL resolution) and the API host are separate env-overridable values.
/// </summary>
public class ShinigamiSource(IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-shinigami";

    public string Name => "shinigami";
    public string DisplayName => "Shinigami";

    public string BaseUrl =>
        Environment.GetEnvironmentVariable("MAKI_SOURCE_SHINIGAMI_BASEURL")?.TrimEnd('/') ?? "https://11.shinigami.asia";

    public SourceCapabilities Capabilities => SourceCapabilities.None;
    public IReadOnlyList<string> SupportedLanguages => ["id"];
    public IReadOnlyList<string> CoverHosts => ["shngm.id"];

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    public string? ResolveSeriesIdFromUrl(Uri url)
    {
        var host = url.Host;
        if (!host.Equals("shinigami.asia", StringComparison.OrdinalIgnoreCase) &&
            !host.EndsWith(".shinigami.asia", StringComparison.OrdinalIgnoreCase) &&
            !StripWww(host).Equals(StripWww(new Uri(BaseUrl).Host), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        const string marker = "/series/";
        var path = url.AbsolutePath;
        var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var tail = path[(index + marker.Length)..].Trim('/').Split('/')[0];
        return Guid.TryParse(tail, out _) ? tail : null;
    }

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var root = await GetAsync($"v1/manga/list?page=1&page_size=30&q={Uri.EscapeDataString(title)}", ct);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<SourceSeriesResult>();
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("manga_id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var id = idEl.GetString()!;
            var name = item.TryGetProperty("title", out var t) ? t.GetString() : null;
            var cover = CoverUrl(item);
            var description = item.TryGetProperty("description", out var d) ? d.GetString() : null;

            results.Add(new SourceSeriesResult(id, name ?? id, $"{BaseUrl}/series/{id}", cover, description));
        }

        return results;
    }

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var root = await GetAsync($"v1/manga/detail/{sourceSeriesId}", ct);
        var data = root.TryGetProperty("data", out var d) ? d : root;

        var title = data.TryGetProperty("title", out var t) ? t.GetString() ?? sourceSeriesId : sourceSeriesId;
        var cover = CoverUrl(data);
        var description = data.TryGetProperty("description", out var desc) ? desc.GetString() : null;
        var status = data.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Number
            ? StatusName(s.GetInt32())
            : null;

        return new SourceSeriesDetail(sourceSeriesId, title, $"{BaseUrl}/series/{sourceSeriesId}", cover, description, status);
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var chapters = new List<SourceChapter>();
        var page = 1;
        while (true)
        {
            var root = await GetAsync($"v1/chapter/{sourceSeriesId}/list?page={page}&page_size=3000", ct);
            if (root.TryGetProperty("data", out var rows) && rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    var chapter = ParseChapter(row, sourceSeriesId);
                    if (chapter is not null)
                    {
                        chapters.Add(chapter);
                    }
                }
            }

            if (!root.TryGetProperty("meta", out var meta) ||
                !meta.TryGetProperty("page", out var pageEl) ||
                !meta.TryGetProperty("total_page", out var totalPageEl) ||
                pageEl.GetInt32() >= totalPageEl.GetInt32())
            {
                break;
            }

            page++;
        }

        return SourceChapterList.Normalize(chapters);
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var root = await GetAsync($"v1/chapter/detail/{chapter.SourceChapterId}", ct);
        var data = root.TryGetProperty("data", out var d) ? d : root;

        var baseUrl = data.TryGetProperty("base_url", out var bu) ? bu.GetString()?.TrimEnd('/') : null;

        var pages = new List<PageRequest>();
        if (!string.IsNullOrEmpty(baseUrl) &&
            data.TryGetProperty("chapter", out var ch) &&
            ch.TryGetProperty("path", out var pathEl) &&
            ch.TryGetProperty("data", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
        {
            // Trimmed on both sides and rejoined with explicit slashes: base_url has none today and
            // path is wrapped in them, but neither is guaranteed to stay that way.
            var path = pathEl.GetString()?.Trim('/');
            if (!string.IsNullOrEmpty(path))
            {
                var headers = new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" };
                foreach (var file in filesEl.EnumerateArray())
                {
                    var name = file.GetString();
                    if (!string.IsNullOrEmpty(name))
                    {
                        pages.Add(new PageRequest($"{baseUrl}/{path}/{name}", headers));
                    }
                }
            }
        }

        if (pages.Count == 0)
        {
            throw new ChapterLockedException($"Chapter {chapter.NumberRaw} of {chapter.SourceSeriesId} served no pages");
        }

        return new ChapterPages(pages);
    }

    private SourceChapter? ParseChapter(JsonElement row, string sourceSeriesId)
    {
        if (row.ValueKind != JsonValueKind.Object ||
            !row.TryGetProperty("chapter_id", out var idEl) || idEl.ValueKind != JsonValueKind.String ||
            !row.TryGetProperty("chapter_number", out var numberEl))
        {
            return null;
        }

        var chapterId = idEl.GetString()!;
        var rawNumber = numberEl.ValueKind == JsonValueKind.Number ? numberEl.GetRawText() : numberEl.GetString();
        var parsed = ChapterNumberParser.Parse(rawNumber);

        var chapterTitle = row.TryGetProperty("chapter_title", out var titleEl) ? titleEl.GetString() : null;

        // A null Number is keyed only by (Volume, Language) in SourceChapterList.Normalize, so a
        // blank title on more than one of them would collapse into a single row. A numbered chapter
        // keeps a null Title; an unparseable one gets a synthetic label to stay distinct.
        string? title;
        if (!string.IsNullOrWhiteSpace(chapterTitle))
        {
            title = chapterTitle;
        }
        else if (parsed.Number is null)
        {
            title = string.IsNullOrEmpty(rawNumber) ? "Extra" : $"Chapter {rawNumber}";
        }
        else
        {
            title = null;
        }

        DateTime? releaseDate = null;
        if (row.TryGetProperty("release_date", out var dateEl) && dateEl.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(dateEl.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d))
        {
            releaseDate = d;
        }

        return new SourceChapter(
            Name,
            sourceSeriesId,
            chapterId,
            rawNumber,
            parsed.Number,
            parsed.Volume,
            Title: title,
            Language: "id",
            ReleaseDate: releaseDate,
            Url: $"{BaseUrl}/chapter/{chapterId}");
    }

    private static string StripWww(string host) =>
        host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

    private static string? CoverUrl(JsonElement item) =>
        NonBlankString(item, "cover_portrait_url") ?? NonBlankString(item, "cover_image_url");

    private static string? NonBlankString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static string StatusName(int status) => status switch
    {
        1 => "Ongoing",
        2 => "Completed",
        3 => "Hiatus",
        _ => status.ToString(CultureInfo.InvariantCulture)
    };

    private async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        // Accept: application/json is set as a default header on the named client (Program.cs);
        // the API otherwise answers the same body regardless, but Keiyoushi always sends it.
        var body = await Client.GetStringAsync(path, ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement.Clone();

        if (root.TryGetProperty("retcode", out var retcode) && retcode.ValueKind == JsonValueKind.Number &&
            retcode.GetInt32() != 0)
        {
            var message = root.TryGetProperty("message", out var m) ? m.GetString() : "unknown error";
            throw new InvalidOperationException($"Shinigami API error: {message}");
        }

        return root;
    }
}
