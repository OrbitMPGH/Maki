using System.Globalization;
using System.Net;
using System.Text.Json;
using Maki.Core.Parsing;
using Maki.Core.Sources;

namespace Maki.Sources.MangaTube;

/// <summary>
/// Manga-Tube (manga-tube.me), German scanlations, plain JSON API. Every request, API included,
/// is first challenged by a home-grown arithmetic anti-bot check (see <see cref="MangaTubeSession"/>);
/// once solved, the resulting <c>__mtbpass</c> cookie is reused for the life of the process.
/// Series id is the slug ("one_piece"); chapter id is the numeric chapter id as a string.
/// Licensed titles either shorten their public chapter list (<c>limitChapters</c>) or 401 the
/// chapters call entirely (<c>{"error":["licence-check"]}</c>). The latter is a stable site
/// state and reads as zero chapters, not a sync failure.
/// </summary>
public class MangaTubeSource(IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-mangatube";

    private readonly MangaTubeSession _session = new(httpClientFactory, HttpClientName);

    public string Name => "mangatube";
    public string DisplayName => "Manga-Tube";
    public string BaseUrl => "https://manga-tube.me";
    public SourceCapabilities Capabilities => SourceCapabilities.None;
    public IReadOnlyList<string> SupportedLanguages => ["de"];
    public IReadOnlyList<string> CoverHosts => ["mtcdn.org"];

    /// <summary>For the live-harness report: whether the challenge was solved and the pass expiry.</summary>
    public (bool Solved, DateTimeOffset? ExpiresAt) ChallengeState => (_session.ChallengeSolved, _session.PassExpiresAt);

    private static readonly TimeZoneInfo BerlinTimeZone = ResolveBerlinTimeZone();

    public string? ResolveSeriesIdFromUrl(Uri url)
    {
        var tail = SourceUrl.PathTail(url, BaseUrl, "/series/");
        return tail is not null && !tail.Contains('/') ? tail : null;
    }

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var root = await GetJsonAsync($"api/manga/quick-search?query={Uri.EscapeDataString(title)}", null, ct);
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<SourceSeriesResult>();
        foreach (var item in data.EnumerateArray())
        {
            var slug = item.TryGetProperty("slug", out var slugEl) ? slugEl.GetString() : null;
            if (string.IsNullOrEmpty(slug))
            {
                continue;
            }

            var name = item.TryGetProperty("title", out var t) ? t.GetString() : null;
            var cover = item.TryGetProperty("cover", out var c) ? c.GetString() : null;
            var url = item.TryGetProperty("url", out var u) ? u.GetString() : null;

            results.Add(new SourceSeriesResult(
                slug,
                string.IsNullOrWhiteSpace(name) ? slug : name,
                string.IsNullOrEmpty(url) ? $"{BaseUrl}/series/{slug}" : $"{BaseUrl}{url}",
                cover));
        }

        return results;
    }

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var root = await GetJsonAsync($"api/manga/{sourceSeriesId}", SeriesHeaders(sourceSeriesId), ct);
        var manga = root.TryGetProperty("data", out var d) && d.TryGetProperty("manga", out var m) ? m : root;

        var title = manga.TryGetProperty("title", out var t) ? t.GetString() : null;
        var cover = manga.TryGetProperty("cover", out var c) ? c.GetString() : null;
        var description = manga.TryGetProperty("description", out var desc) ? desc.GetString() : null;
        var status = manga.TryGetProperty("status", out var s) ? MapStatus(s) : null;

        return new SourceSeriesDetail(
            sourceSeriesId,
            string.IsNullOrWhiteSpace(title) ? sourceSeriesId : title,
            $"{BaseUrl}/series/{sourceSeriesId}",
            cover,
            string.IsNullOrWhiteSpace(description) ? null : description,
            status);
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var response = await _session.SendAsync(
            HttpMethod.Get, $"api/manga/{sourceSeriesId}/chapters", SeriesHeaders(sourceSeriesId), ct);

        // A fully licensed-out title 401s here rather than 200ing an empty list. That is a
        // stable site state (the title lost its public chapters, not a broken request), so it
        // reads as zero chapters instead of throwing out of a monitored series' sync.
        if (response.Status == HttpStatusCode.Unauthorized)
        {
            return [];
        }

        var root = response.AsJson();
        var rows = root.TryGetProperty("data", out var d) && d.TryGetProperty("chapters", out var c) && c.ValueKind == JsonValueKind.Array
            ? c
            : default;
        if (rows.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var chapters = new List<SourceChapter>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("id", out var idEl))
            {
                continue;
            }

            var hidden = row.TryGetProperty("hidden", out var hiddenEl) && hiddenEl.ValueKind == JsonValueKind.True;
            var published = !row.TryGetProperty("published", out var publishedEl) || publishedEl.ValueKind != JsonValueKind.False;
            if (hidden || !published)
            {
                continue;
            }

            var chapterId = idEl.ValueKind == JsonValueKind.Number
                ? idEl.GetInt64().ToString(CultureInfo.InvariantCulture)
                : idEl.GetString();
            if (string.IsNullOrEmpty(chapterId))
            {
                continue;
            }

            var number = row.TryGetProperty("number", out var numEl) && numEl.ValueKind == JsonValueKind.Number ? numEl.GetInt32() : 0;
            var subNumber = row.TryGetProperty("subNumber", out var subEl) && subEl.ValueKind == JsonValueKind.Number ? subEl.GetInt32() : 0;
            var volume = row.TryGetProperty("volume", out var volEl) && volEl.ValueKind == JsonValueKind.Number ? volEl.GetInt32() : 0;
            var name = row.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            var publishedAt = row.TryGetProperty("publishedAt", out var pubAtEl) ? pubAtEl.GetString() : null;

            // Keiyoushi's convention: a non-zero subNumber is appended as a decimal ("12.1").
            var numberRaw = subNumber == 0
                ? number.ToString(CultureInfo.InvariantCulture)
                : $"{number}.{subNumber}";

            var parsed = ChapterNumberParser.Parse(numberRaw, volume > 0 ? volume.ToString(CultureInfo.InvariantCulture) : null);

            var chapterTitle = string.IsNullOrWhiteSpace(name) ? null : name;
            if (parsed.Number is null && chapterTitle is null)
            {
                // ChapterIdentity dedupes null-number chapters by Title; a null Title here
                // would collapse every unparseable chapter of this series into one row.
                chapterTitle = $"Chapter {numberRaw}";
            }

            chapters.Add(new SourceChapter(
                Name,
                sourceSeriesId,
                chapterId,
                numberRaw,
                parsed.Number,
                parsed.Volume,
                Title: chapterTitle,
                Language: "de",
                ReleaseDate: ParseReleaseDate(publishedAt),
                Url: $"{BaseUrl}/series/{sourceSeriesId}/read/{chapterId}"));
        }

        return SourceChapterList.Normalize(chapters);
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var response = await _session.SendAsync(
            HttpMethod.Get,
            $"api/manga/{chapter.SourceSeriesId}/chapter/{chapter.SourceChapterId}",
            SeriesHeaders(chapter.SourceSeriesId),
            ct);

        if (response.Status == HttpStatusCode.Unauthorized)
        {
            throw new ChapterLockedException(
                $"Chapter {chapter.NumberRaw} of {chapter.SourceSeriesId} is licence-restricted on Manga-Tube.");
        }

        var root = response.AsJson();
        var pageArray = root.TryGetProperty("data", out var d) &&
                        d.TryGetProperty("chapter", out var ch) &&
                        ch.TryGetProperty("pages", out var p) &&
                        p.ValueKind == JsonValueKind.Array
            ? p
            : default;

        var ordered = new List<(int Page, string Url)>();
        if (pageArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var page in pageArray.EnumerateArray())
            {
                var pageNumber = page.TryGetProperty("page", out var pageEl) && pageEl.ValueKind == JsonValueKind.Number
                    ? pageEl.GetInt32()
                    : int.MaxValue;

                var url = page.TryGetProperty("url", out var urlEl) ? urlEl.GetString() : null;
                if (string.IsNullOrEmpty(url))
                {
                    url = page.TryGetProperty("alt_source", out var altEl) ? altEl.GetString() : null;
                }

                if (!string.IsNullOrEmpty(url))
                {
                    ordered.Add((pageNumber, url));
                }
            }
        }

        var headers = new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" };
        var pages = ordered
            .OrderBy(page => page.Page)
            .Select(page => new PageRequest(page.Url, headers))
            .ToList();

        if (pages.Count == 0)
        {
            throw new ChapterLockedException(
                $"Chapter {chapter.NumberRaw} of {chapter.SourceSeriesId} has no pages on Manga-Tube.");
        }

        return new ChapterPages(pages);
    }

    private static Dictionary<string, string> SeriesHeaders(string sourceSeriesId) => new()
    {
        ["Referer"] = $"https://manga-tube.me/series/{sourceSeriesId}",
        ["Use-Parameter"] = "manga_slug",
    };

    private async Task<JsonElement> GetJsonAsync(string path, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        var response = await _session.SendAsync(HttpMethod.Get, path, headers, ct);
        return response.AsJson();
    }

    private static string? MapStatus(JsonElement statusEl)
    {
        if (statusEl.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return statusEl.GetInt32() switch
        {
            1 => "Ongoing",
            2 => "Completed",
            _ => null,
        };
    }

    /// <summary>publishedAt carries no timezone; the site is German, so treated as Europe/Berlin
    /// and converted to UTC. Falls back to UTC itself if the host has no timezone database.</summary>
    private static DateTime? ParseReleaseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!DateTime.TryParseExact(
                raw, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, BerlinTimeZone);
    }

    private static TimeZoneInfo ResolveBerlinTimeZone()
    {
        foreach (var id in new[] { "Europe/Berlin", "W. Europe Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Utc;
    }
}
