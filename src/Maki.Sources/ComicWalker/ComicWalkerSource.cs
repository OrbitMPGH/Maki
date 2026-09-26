using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Maki.Core.Sources;

namespace Maki.Sources.ComicWalker;

/// <summary>
/// KadoComi (formerly Comic Walker): Kadokawa's official free-manga site. A private,
/// unversioned JSON API behind CloudFront; a plain browser-UA client is enough, no
/// challenge. Search only matches Japanese text (romaji gets zero hits), so auto-match
/// works only for series whose stored title is Japanese.
///
/// Most episodes are inactive at any moment (paid until a later free campaign), so the
/// chapter list only ever shows the free window: the first couple plus the newest few.
/// That is correct behaviour, not a bug.
///
/// Pages are served XOR-encrypted per page (drmHash), the same scheme MANGA Plus uses,
/// so they ride PageRequest.XorKeyHex and are decrypted by the downloader.
/// </summary>
public partial class ComicWalkerSource(IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-comicwalker";

    [GeneratedRegex(@"^KC_\d+_S$")]
    private static partial Regex SeriesIdPattern();

    public string Name => "comicwalker";
    public string DisplayName => "KadoComi (Comic Walker)";
    public string BaseUrl => "https://comic-walker.com";
    public SourceCapabilities Capabilities => SourceCapabilities.None;
    public IReadOnlyList<string> SupportedLanguages => ["ja"];

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    public string? ResolveSeriesIdFromUrl(Uri url)
    {
        var tail = SourceUrl.PathTail(url, BaseUrl, "/detail/", firstSegmentOnly: false);
        return tail is not null && !tail.Contains('/') && SeriesIdPattern().IsMatch(tail) ? tail : null;
    }

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var url = $"api/search/keywords?keywords={Uri.EscapeDataString(title)}&limit=30&offset=0";
        var (body, root) = await GetJsonAsync(url, ct);
        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
        {
            throw Unexpected(url, body);
        }

        var results = new List<SourceSeriesResult>();
        foreach (var item in result.EnumerateArray())
        {
            var code = item.TryGetProperty("code", out var codeEl) ? codeEl.GetString() : null;
            if (string.IsNullOrEmpty(code))
            {
                continue;
            }

            var seriesTitle = item.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
            results.Add(new SourceSeriesResult(code, seriesTitle ?? code, $"{BaseUrl}/detail/{code}", Cover(item)));
        }

        return results;
    }

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var root = await GetWorkAsync(sourceSeriesId, ct);
        var work = root.GetProperty("work");

        var title = work.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
        var summary = work.TryGetProperty("summary", out var summaryEl) ? summaryEl.GetString() : null;
        var status = work.TryGetProperty("serializationStatus", out var statusEl) ? statusEl.GetString() : null;

        return new SourceSeriesDetail(
            sourceSeriesId,
            title ?? sourceSeriesId,
            $"{BaseUrl}/detail/{sourceSeriesId}",
            Cover(work),
            summary,
            status switch
            {
                "ongoing" => "Ongoing",
                "finished" => "Completed",
                _ => null
            });
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var root = await GetWorkAsync(sourceSeriesId, ct);
        var work = root.GetProperty("work");
        var language = work.TryGetProperty("language", out var languageEl) ? languageEl.GetString() : null;
        language = string.IsNullOrEmpty(language) ? "ja" : language;

        if (!root.TryGetProperty("latestEpisodes", out var latestEpisodes) ||
            !latestEpisodes.TryGetProperty("result", out var episodes) ||
            episodes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var chapters = new List<SourceChapter>();
        foreach (var episode in episodes.EnumerateArray())
        {
            // Only free-to-read regular episodes. "pr" is site announcements, and an
            // inactive episode is paid until a later campaign frees it; the next sync
            // picks those up once isActive flips.
            var type = episode.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
            var isActive = episode.TryGetProperty("isActive", out var activeEl) && activeEl.ValueKind == JsonValueKind.True;
            if (type != "normal" || !isActive)
            {
                continue;
            }

            var id = episode.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var code = episode.TryGetProperty("code", out var codeEl) ? codeEl.GetString() : null;
            var episodeTitle = episode.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(code) || string.IsNullOrEmpty(episodeTitle))
            {
                continue;
            }

            var subTitle = episode.TryGetProperty("subTitle", out var subTitleEl) ? subTitleEl.GetString() : null;
            var displayTitle = string.IsNullOrEmpty(subTitle) ? episodeTitle : $"{episodeTitle} - {subTitle}";

            DateTime? releaseDate = episode.TryGetProperty("updateDate", out var dateEl) &&
                                     dateEl.ValueKind == JsonValueKind.String &&
                                     DateTime.TryParse(
                                         dateEl.GetString(), CultureInfo.InvariantCulture,
                                         DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsedDate)
                ? parsedDate
                : null;

            chapters.Add(new SourceChapter(
                Name,
                sourceSeriesId,
                id,
                episodeTitle,
                ComicWalkerChapterNumber.Parse(episodeTitle),
                Volume: null,
                Title: displayTitle,
                Language: language,
                ReleaseDate: releaseDate,
                Url: $"{BaseUrl}/detail/{sourceSeriesId}/episodes/{code}"));
        }

        return SourceChapterList.Normalize(chapters);
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var url = $"api/contents/viewer?episodeId={Uri.EscapeDataString(chapter.SourceChapterId)}&imageSizeType=width:1284";
        var (body, root) = await GetJsonAsync(url, ct);
        if (!root.TryGetProperty("manuscripts", out var manuscripts) || manuscripts.ValueKind != JsonValueKind.Array)
        {
            throw Unexpected(url, body);
        }

        if (manuscripts.GetArrayLength() == 0)
        {
            throw new ChapterLockedException(
                $"Episode {chapter.SourceChapterId} has no free pages (its free window closed)");
        }

        var pages = new List<(int Page, PageRequest Request)>();
        foreach (var manuscript in manuscripts.EnumerateArray())
        {
            var drmMode = manuscript.TryGetProperty("drmMode", out var drmModeEl) ? drmModeEl.GetString() : null;
            if (drmMode != "xor")
            {
                throw new NotSupportedException(
                    $"Unsupported drmMode '{drmMode}' on episode {chapter.SourceChapterId}");
            }

            var page = manuscript.TryGetProperty("page", out var pageEl) ? pageEl.GetInt32() : 0;
            var imageUrl = manuscript.TryGetProperty("drmImageUrl", out var imageUrlEl) ? imageUrlEl.GetString() : null;
            var drmHash = manuscript.TryGetProperty("drmHash", out var drmHashEl) ? drmHashEl.GetString() : null;
            if (string.IsNullOrEmpty(imageUrl) || string.IsNullOrEmpty(drmHash))
            {
                throw Unexpected(url, body);
            }

            pages.Add((page, new PageRequest(imageUrl, Headers: null, XorKeyHex: drmHash)));
        }

        return new ChapterPages(pages.OrderBy(p => p.Page).Select(p => p.Request).ToList());
    }

    private static string? Cover(JsonElement item)
    {
        if (item.TryGetProperty("bookCover", out var bookCover) && bookCover.GetString() is { Length: > 0 } cover)
        {
            return cover;
        }

        return item.TryGetProperty("originalThumbnail", out var originalThumbnail) ? originalThumbnail.GetString() : null;
    }

    private async Task<JsonElement> GetWorkAsync(string workCode, CancellationToken ct)
    {
        var url = $"api/contents/details/work?workCode={Uri.EscapeDataString(workCode)}";
        var (body, root) = await GetJsonAsync(url, ct);
        if (!root.TryGetProperty("work", out _))
        {
            throw Unexpected(url, body);
        }

        return root;
    }

    private async Task<(string Body, JsonElement Root)> GetJsonAsync(string url, CancellationToken ct)
    {
        var body = await Client.GetStringAsync(url, ct);
        try
        {
            using var doc = JsonDocument.Parse(body);
            return (body, doc.RootElement.Clone());
        }
        catch (JsonException)
        {
            throw Unexpected(url, body);
        }
    }

    private static InvalidOperationException Unexpected(string url, string body) =>
        new($"Unexpected response from {url}: {(body.Length <= 100 ? body : body[..100])}");
}
