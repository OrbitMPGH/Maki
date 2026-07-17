using System.Text;
using System.Text.Json;
using Mangarr.Core.Parsing;
using Mangarr.Core.Sources;

namespace Mangarr.Sources.MangaPlus;

/// <summary>
/// MANGA Plus by Shueisha — the official free same-day English source for Shonen
/// Jump titles (One Piece, Kagurabachi, Dandadan, Jujutsu Kaisen…).
///
/// Uses the app web API with ?format=json (no protobuf). Page images are XOR-encrypted
/// with a per-image hex key returned beside each page; the key rides on the PageRequest
/// and PageDownloader decrypts the fetched bytes.
///
/// Only the first- and latest-few chapters of each title are free — older chapters error
/// at the viewer endpoint (the grab then fails, which is expected: this source exists for
/// brand-new chapters). The API bans datacenter IPs, so it only works from a residential
/// IP (e.g. a home server), not most cloud hosts. There is no search endpoint, so we filter
/// the (cached) full catalog by normalized title.
/// </summary>
public class MangaPlusSource(IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-mangaplus";

    // English titles carry language 0 or an absent field.
    private static readonly int?[] English = [0, null];
    private static readonly TimeSpan CatalogTtl = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _catalogLock = new(1, 1);
    private List<SourceSeriesResult> _catalog = [];
    private DateTime _catalogAt = DateTime.MinValue;

    public string Name => "mangaplus";
    public string DisplayName => "MANGA Plus";
    public string BaseUrl => "https://mangaplus.shueisha.co.jp";
    public SourceCapabilities Capabilities => SourceCapabilities.None;

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    public string? ResolveSeriesIdFromUrl(Uri url) =>
        // https://mangaplus.shueisha.co.jp/titles/{id}
        SourceUrl.PathTail(url, BaseUrl, "/titles/", firstSegmentOnly: true);

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var query = Normalize(title);
        if (query.Length == 0)
        {
            return [];
        }

        var scored = new List<(int Score, SourceSeriesResult Series)>();
        foreach (var series in await LoadCatalogAsync(ct))
        {
            var name = Normalize(series.Title);
            int score;
            if (name == query)
            {
                score = 3;
            }
            else if (name.StartsWith(query, StringComparison.Ordinal) || query.StartsWith(name, StringComparison.Ordinal))
            {
                score = 2;
            }
            else if (name.Contains(query, StringComparison.Ordinal) || query.Contains(name, StringComparison.Ordinal))
            {
                score = 1;
            }
            else
            {
                continue;
            }

            scored.Add((score, series));
        }

        return scored.OrderByDescending(s => s.Score).Select(s => s.Series).ToList();
    }

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var data = await GetAsync("title_detailV3", ct, ("title_id", sourceSeriesId));
        var title = sourceSeriesId;
        string? cover = null;
        string? description = null;

        if (data.TryGetProperty("titleDetailView", out var view))
        {
            if (view.TryGetProperty("title", out var t))
            {
                title = t.TryGetProperty("name", out var n) ? n.GetString() ?? sourceSeriesId : sourceSeriesId;
                cover = t.TryGetProperty("portraitImageUrl", out var c) ? c.GetString() : null;
            }

            description = view.TryGetProperty("overview", out var o) ? o.GetString() : null;
        }

        return new SourceSeriesDetail(sourceSeriesId, title, $"{BaseUrl}/titles/{sourceSeriesId}", cover, description);
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var data = await GetAsync("title_detailV3", ct, ("title_id", sourceSeriesId));
        if (!data.TryGetProperty("titleDetailView", out var view) ||
            !view.TryGetProperty("chapterListGroup", out var groups))
        {
            return [];
        }

        var chapters = new List<SourceChapter>();
        foreach (var group in groups.EnumerateArray())
        {
            foreach (var listName in new[] { "firstChapterList", "midChapterList", "lastChapterList" })
            {
                if (!group.TryGetProperty(listName, out var list))
                {
                    continue;
                }

                foreach (var ch in list.EnumerateArray())
                {
                    if (!ch.TryGetProperty("chapterId", out var idEl))
                    {
                        continue;
                    }

                    var chapterId = idEl.ValueKind == JsonValueKind.Number
                        ? idEl.GetInt64().ToString()
                        : idEl.GetString();
                    if (string.IsNullOrEmpty(chapterId))
                    {
                        continue;
                    }

                    // name is like "#12"; "ex" / one-shots have no number
                    var raw = (ch.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null)?.TrimStart('#');
                    var parsed = ChapterNumberParser.Parse(raw);
                    var subTitle = ch.TryGetProperty("subTitle", out var st) ? st.GetString() : null;

                    chapters.Add(new SourceChapter(
                        Name,
                        sourceSeriesId,
                        chapterId,
                        raw,
                        parsed.Number,
                        parsed.Volume,
                        Title: string.IsNullOrWhiteSpace(subTitle) ? null : subTitle,
                        Language: "en",
                        ReleaseDate: null,
                        Url: $"{BaseUrl}/viewer/{chapterId}"));
                }
            }
        }

        return SourceChapterList.Normalize(chapters);
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var data = await GetAsync(
            "manga_viewer", ct,
            ("chapter_id", chapter.SourceChapterId),
            ("split", "yes"),
            ("img_quality", "high"));

        var pages = new List<PageRequest>();
        if (data.TryGetProperty("mangaViewer", out var viewer) && viewer.TryGetProperty("pages", out var pageArray))
        {
            foreach (var page in pageArray.EnumerateArray())
            {
                // banners / ads have no mangaPage
                if (!page.TryGetProperty("mangaPage", out var mangaPage))
                {
                    continue;
                }

                var imageUrl = mangaPage.TryGetProperty("imageUrl", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(imageUrl))
                {
                    continue;
                }

                var key = mangaPage.TryGetProperty("encryptionKey", out var k) ? k.GetString() : null;
                pages.Add(new PageRequest(imageUrl, Headers: null, XorKeyHex: key));
            }
        }

        return new ChapterPages(pages);
    }

    private async Task<List<SourceSeriesResult>> LoadCatalogAsync(CancellationToken ct)
    {
        if (_catalog.Count > 0 && DateTime.UtcNow - _catalogAt < CatalogTtl)
        {
            return _catalog;
        }

        await _catalogLock.WaitAsync(ct);
        try
        {
            if (_catalog.Count > 0 && DateTime.UtcNow - _catalogAt < CatalogTtl)
            {
                return _catalog;
            }

            var data = await GetAsync("title_list/allV2", ct);
            var catalog = new List<SourceSeriesResult>();
            var seen = new HashSet<string>();

            if (data.TryGetProperty("allTitlesViewV2", out var view) &&
                view.TryGetProperty("AllTitlesGroup", out var groups))
            {
                foreach (var group in groups.EnumerateArray())
                {
                    if (!group.TryGetProperty("titles", out var titles))
                    {
                        continue;
                    }

                    foreach (var title in titles.EnumerateArray())
                    {
                        var language = title.TryGetProperty("language", out var lang) && lang.ValueKind == JsonValueKind.Number
                            ? lang.GetInt32()
                            : (int?)null;
                        if (Array.IndexOf(English, language) < 0)
                        {
                            continue;
                        }

                        if (!title.TryGetProperty("titleId", out var idEl))
                        {
                            continue;
                        }

                        var id = idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64().ToString() : idEl.GetString();
                        var name = title.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name) || !seen.Add(id))
                        {
                            continue;
                        }

                        var cover = title.TryGetProperty("portraitImageUrl", out var c) ? c.GetString() : null;
                        catalog.Add(new SourceSeriesResult(id, name, $"{BaseUrl}/titles/{id}", cover));
                    }
                }
            }

            _catalog = catalog;
            _catalogAt = DateTime.UtcNow;
            return catalog;
        }
        finally
        {
            _catalogLock.Release();
        }
    }

    /// <summary>
    /// GETs an API path with the fixed ?format=json client params plus any extras, and
    /// returns the "success" payload (throwing the site's error popup text on failure).
    /// </summary>
    private async Task<JsonElement> GetAsync(string path, CancellationToken ct, params (string Key, string Value)[] parameters)
    {
        var query = new StringBuilder("?format=json&os=android&os_ver=32&app_ver=40");
        foreach (var (key, value) in parameters)
        {
            query.Append('&').Append(key).Append('=').Append(Uri.EscapeDataString(value));
        }

        var body = await Client.GetStringAsync(path + query, ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var error))
        {
            var subject = error.TryGetProperty("englishPopup", out var popup) &&
                          popup.TryGetProperty("subject", out var s)
                ? s.GetString()
                : null;
            throw new InvalidOperationException(subject ?? "MangaPlus API error");
        }

        return root.TryGetProperty("success", out var success) ? success.Clone() : default;
    }

    private static string Normalize(string? text) =>
        text is null ? string.Empty : new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
