using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Maki.Core.Parsing;
using Maki.Core.Sources;

namespace Maki.Sources.MangaLib;

/// <summary>
/// MangaLib (LibGroup network) source. JSON API behind DDoS-Guard, not Cloudflare, so a plain
/// client works as long as every request carries a mangalib Referer. The series id is the API's
/// own <c>slug_url</c> ("{id}--{slug}"); the chapter id packs volume, number and the chosen
/// translation branch as "{volume}|{number}|{branchId}" so <see cref="GetPagesAsync"/> can rebuild
/// the per-chapter endpoint without a second series lookup. Image URLs come from the "download"
/// image server (<c>/api/constants</c>), never "main": that one serves AVIF bytes under a
/// <c>.jpg</c> name, which ImageSharp cannot decode.
/// </summary>
public partial class MangaLibSource(IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-mangalib";

    public string Name => "mangalib";
    public string DisplayName => "MangaLib";
    public string BaseUrl => "https://mangalib.me";
    public SourceCapabilities Capabilities => SourceCapabilities.None;
    public SourceContent Content => SourceContent.Manga | SourceContent.Manhwa;
    public IReadOnlyList<string> SupportedLanguages => ["ru"];
    public IReadOnlyList<string> CoverHosts => ["cdnlibs.org"];

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    private readonly SemaphoreSlim _imageServerGate = new(1, 1);
    private string? _downloadServerUrl;

    [GeneratedRegex(@"^\d+--")]
    private static partial Regex SeriesIdPattern();

    public string? ResolveSeriesIdFromUrl(Uri url)
    {
        var tail = SourceUrl.PathTail(url, BaseUrl, "/manga/", firstSegmentOnly: true);
        return tail is not null && SeriesIdPattern().IsMatch(tail) ? tail : null;
    }

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var root = await GetJsonAsync($"api/manga?q={Uri.EscapeDataString(title)}&site_id%5B%5D=1", ct);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<SourceSeriesResult>();
        foreach (var item in data.EnumerateArray())
        {
            var slugUrl = item.TryGetProperty("slug_url", out var su) ? su.GetString() : null;
            if (string.IsNullOrEmpty(slugUrl))
            {
                continue;
            }

            results.Add(new SourceSeriesResult(
                slugUrl,
                PickSearchTitle(item),
                $"{BaseUrl}/ru/manga/{slugUrl}",
                PickCover(item)));
        }

        return results;
    }

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var root = await GetJsonAsync(
            $"api/manga/{sourceSeriesId}?fields%5B%5D=summary&fields%5B%5D=eng_name&fields%5B%5D=otherNames", ct);
        var data = root.TryGetProperty("data", out var d) ? d : root;

        return new SourceSeriesDetail(
            sourceSeriesId,
            PickTitle(data),
            $"{BaseUrl}/ru/manga/{sourceSeriesId}",
            PickCover(data),
            BuildDescription(data),
            MapStatus(data));
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var root = await GetJsonAsync($"api/manga/{sourceSeriesId}/chapters", ct);
        if (!root.TryGetProperty("data", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var chapters = new List<SourceChapter>();
        foreach (var row in rows.EnumerateArray())
        {
            var number = row.TryGetProperty("number", out var numEl) ? numEl.GetString() : null;

            if (!row.TryGetProperty("branches", out var branches) || branches.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            // Multiple branches are competing Russian translations of the same chapter, not
            // languages, so one row per chapter is correct: take the first branch that isn't
            // behind a closed early-access window. If every branch is closed, the site is
            // telling us so; skip the chapter rather than list something nothing can fetch.
            JsonElement? chosen = null;
            foreach (var branch in branches.EnumerateArray())
            {
                var closed = branch.TryGetProperty("restricted_view", out var rv) &&
                             rv.ValueKind == JsonValueKind.Object &&
                             rv.TryGetProperty("is_open", out var isOpenEl) &&
                             isOpenEl.ValueKind == JsonValueKind.False;
                if (!closed)
                {
                    chosen = branch;
                    break;
                }
            }

            if (chosen is null)
            {
                continue;
            }

            var volume = row.TryGetProperty("volume", out var volEl) ? volEl.GetString() : null;
            var parsed = ChapterNumberParser.Parse(number, volume);
            var name = row.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            var branchId = chosen.Value.TryGetProperty("branch_id", out var bidEl) && bidEl.ValueKind == JsonValueKind.Number
                ? bidEl.GetInt64().ToString(CultureInfo.InvariantCulture)
                : null;
            var createdAt = chosen.Value.TryGetProperty("created_at", out var createdEl) &&
                             createdEl.ValueKind == JsonValueKind.String &&
                             DateTime.TryParse(
                                 createdEl.GetString(), CultureInfo.InvariantCulture,
                                 DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var created)
                ? created
                : (DateTime?)null;

            var title = string.IsNullOrWhiteSpace(name) ? null : name;
            if (parsed.Number is null && title is null)
            {
                // A one-shot/extra's identity is IsOneShot + Language + Title, Volume ignored, so
                // a blank title here would alias every untitled extra on this series into one row.
                title = string.IsNullOrEmpty(volume) ? "Extra" : $"Vol. {volume} extra";
            }

            chapters.Add(new SourceChapter(
                Name,
                sourceSeriesId,
                $"{volume}|{number}|{branchId}",
                number,
                parsed.Number,
                parsed.Volume,
                Title: title,
                Language: "ru",
                ReleaseDate: createdAt,
                Url: $"{BaseUrl}/ru/{sourceSeriesId}/read/v{volume}/c{number}"));
        }

        return SourceChapterList.Normalize(chapters);
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var parts = chapter.SourceChapterId.Split('|');
        var volume = parts.Length > 0 ? parts[0] : string.Empty;
        var number = parts.Length > 1 ? parts[1] : string.Empty;
        var branchId = parts.Length > 2 ? parts[2] : string.Empty;

        var query = $"api/manga/{chapter.SourceSeriesId}/chapter" +
                    $"?number={Uri.EscapeDataString(number)}&volume={Uri.EscapeDataString(volume)}";
        if (!string.IsNullOrEmpty(branchId))
        {
            query += $"&branch_id={Uri.EscapeDataString(branchId)}";
        }

        var root = await GetJsonAsync(query, ct);
        var data = root.TryGetProperty("data", out var d) ? d : root;

        var pages = new List<(int Slug, string Url)>();
        if (data.TryGetProperty("pages", out var pagesEl) && pagesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var page in pagesEl.EnumerateArray())
            {
                var pageUrl = page.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(pageUrl))
                {
                    continue;
                }

                var slug = page.TryGetProperty("slug", out var s) && s.ValueKind == JsonValueKind.Number
                    ? s.GetInt32()
                    : int.MaxValue;
                pages.Add((slug, pageUrl));
            }
        }

        if (pages.Count == 0)
        {
            // No listed-locked signal survives to this endpoint (restricted_view lives on the
            // chapter-list branch, not here), so an empty result reads the same whether the
            // chapter is gone or still behind early access. Treat it as locked: the download
            // pipeline retries a ChapterLockedException instead of failing the item outright.
            throw new ChapterLockedException(
                $"MangaLib chapter {chapter.SourceChapterId} of {chapter.SourceSeriesId} served no pages");
        }

        var downloadServer = await GetDownloadServerUrlAsync(ct);
        var headers = new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" };

        var requests = pages
            .OrderBy(p => p.Slug)
            // The site's own page urls begin with "//", so this concatenation is the double
            // slash the site itself serves, not a bug.
            .Select(p => new PageRequest($"{downloadServer}{p.Url}", headers))
            .ToList();

        return new ChapterPages(requests);
    }

    private async Task<string> GetDownloadServerUrlAsync(CancellationToken ct)
    {
        if (_downloadServerUrl is not null)
        {
            return _downloadServerUrl;
        }

        await _imageServerGate.WaitAsync(ct);
        try
        {
            if (_downloadServerUrl is not null)
            {
                return _downloadServerUrl;
            }

            string? download = null;
            string? compress = null;
            var root = await GetJsonAsync("api/constants?fields%5B%5D=imageServers", ct);
            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("imageServers", out var servers) &&
                servers.ValueKind == JsonValueKind.Array)
            {
                foreach (var server in servers.EnumerateArray())
                {
                    if (!server.TryGetProperty("site_ids", out var siteIds) ||
                        siteIds.ValueKind != JsonValueKind.Array ||
                        !siteIds.EnumerateArray().Any(s => s.ValueKind == JsonValueKind.Number && s.GetInt32() == 1))
                    {
                        continue;
                    }

                    var id = server.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    var url = server.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
                    if (string.IsNullOrEmpty(url))
                    {
                        continue;
                    }

                    if (id == "download")
                    {
                        download ??= url;
                    }
                    else if (id == "compress")
                    {
                        compress ??= url;
                    }
                }
            }

            // Fallback matches today's live value for site_id 1, so a transient /api/constants
            // hiccup doesn't take down every page fetch.
            _downloadServerUrl = download ?? compress ?? "https://img3.cdnlibs.org";
            return _downloadServerUrl;
        }
        finally
        {
            _imageServerGate.Release();
        }
    }

    private static string PickTitle(JsonElement item)
    {
        return GetNonEmptyString(item, "rus_name")
            ?? GetNonEmptyString(item, "eng_name")
            ?? GetNonEmptyString(item, "name")
            ?? "Unknown";
    }

    // Search titles feed SourceMatchService, which scores against English/romaji titles only.
    private static string PickSearchTitle(JsonElement item)
    {
        return GetNonEmptyString(item, "eng_name")
            ?? GetNonEmptyString(item, "name")
            ?? GetNonEmptyString(item, "rus_name")
            ?? "Unknown";
    }

    private static string? PickCover(JsonElement item)
    {
        if (!item.TryGetProperty("cover", out var cover) || cover.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return GetNonEmptyString(cover, "default")
            ?? GetNonEmptyString(cover, "md")
            ?? GetNonEmptyString(cover, "thumbnail");
    }

    private static string? BuildDescription(JsonElement data)
    {
        if (!data.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.Object ||
            !summary.TryGetProperty("content", out var paragraphs) || paragraphs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var lines = new List<string>();
        foreach (var paragraph in paragraphs.EnumerateArray())
        {
            if (!paragraph.TryGetProperty("content", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var text = string.Concat(nodes.EnumerateArray()
                .Where(n => n.TryGetProperty("type", out var t) && t.GetString() == "text")
                .Select(n => n.TryGetProperty("text", out var txt) ? txt.GetString() : null));
            if (!string.IsNullOrEmpty(text))
            {
                lines.Add(text);
            }
        }

        return lines.Count > 0 ? string.Join("\n", lines) : null;
    }

    private static string? MapStatus(JsonElement data)
    {
        if (!data.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Object ||
            !status.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return idEl.GetInt32() switch
        {
            1 => "Ongoing",
            2 => "Completed",
            _ => null
        };
    }

    private static string? GetNonEmptyString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        var body = await Client.GetStringAsync(path, ct);
        var trimmed = body.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            throw new HttpRequestException(
                $"MangaLib API returned non-JSON content for {path} (DDoS-Guard challenge?)");
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }
}
