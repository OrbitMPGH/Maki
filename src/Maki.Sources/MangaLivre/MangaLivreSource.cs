using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Maki.Core.Sources;

namespace Maki.Sources.MangaLivre;

/// <summary>
/// Manga Livre scraper — Brazilian Portuguese, <c>mangalivre.to</c>. The dominant BR manga site
/// has moved domains repeatedly under DMCA pressure (previously <c>.tv</c>/<c>.one</c>/others);
/// this is the address live as of writing. Search is the standard WordPress/Madara AJAX endpoint
/// (<c>admin-ajax.php?action=wp-manga-search-manga</c>), but the rest of the theme is customized
/// — chapter lists are <c>div.chapter-box</c>, not Madara's usual <c>li.wp-manga-chapter</c>.
/// <para>
/// Chapter labels are Portuguese ("Capitulo 1190"), which <c>ChapterNumberParser</c>'s English
/// "ch"/"chapter" patterns don't recognize, so the chapter number is read off the URL slug
/// (<c>capitulo-1190</c>, or <c>capitulo-1189_1</c> for a ".1" sub-chapter) instead of the label.
/// Release dates are rendered in Portuguese ("22 de agosto de 2026") and parsed by hand against a
/// month-name table rather than via a Portuguese <see cref="CultureInfo"/>, so a date parse never
/// depends on which locale happens to be installed.
/// </para>
/// </summary>
public partial class MangaLivreSource(IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-mangalivre";

    private static readonly HtmlParser Parser = new();

    public string Name => "mangalivre";
    public string DisplayName => "Manga Livre";
    public string BaseUrl => "https://mangalivre.to";
    public SourceCapabilities Capabilities => SourceCapabilities.None;
    public IReadOnlyList<string> SupportedLanguages => ["pt-BR"];

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    [GeneratedRegex(@"capitulo-(\d+)(?:_(\d+))?", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterSlugRegex();

    private static readonly Dictionary<string, int> PortugueseMonths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["janeiro"] = 1, ["fevereiro"] = 2, ["março"] = 3, ["abril"] = 4,
        ["maio"] = 5, ["junho"] = 6, ["julho"] = 7, ["agosto"] = 8,
        ["setembro"] = 9, ["outubro"] = 10, ["novembro"] = 11, ["dezembro"] = 12
    };

    [GeneratedRegex(@"^(\d{1,2})\s+de\s+(\p{L}+)\s+de\s+(\d{4})$", RegexOptions.IgnoreCase)]
    private static partial Regex PortugueseDateRegex();

    public string? ResolveSeriesIdFromUrl(Uri url) =>
        SourceUrl.PathTail(url, BaseUrl, "/manga/", firstSegmentOnly: true);

    // ── Search ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        using var response = await Client.PostAsync(
            "wp-admin/admin-ajax.php",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["action"] = "wp-manga-search-manga",
                ["title"] = title
            }),
            ct);
        response.EnsureSuccessStatusCode();

        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<SourceSeriesResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in data.EnumerateArray())
        {
            var url = entry.TryGetProperty("url", out var u) ? u.GetString() : null;
            var entryTitle = entry.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(entryTitle) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                continue;
            }

            var seriesId = SourceUrl.PathTail(uri, BaseUrl, "/manga/", firstSegmentOnly: true);
            if (seriesId is null || !seen.Add(seriesId))
            {
                continue;
            }

            results.Add(new SourceSeriesResult(seriesId, entryTitle, url));
        }

        return results;
    }

    // ── Series detail ─────────────────────────────────────────────────

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var doc = await GetHtmlAsync($"manga/{sourceSeriesId}/", ct);

        var title = doc.QuerySelector("div.post-title h1")?.TextContent.Trim() ?? sourceSeriesId;
        var cover = doc.QuerySelector("div.summary_image img")?.GetAttribute("src")?.Trim();
        // .summary__content carries the real synopsis in its first <p>, followed by the site's
        // own SEO filler paragraphs (cross-promo, "other things to follow" etc.) — only the first
        // paragraph is the actual description.
        var description = doc.QuerySelector("div.summary__content p")?.TextContent.Trim();
        var status = StatusOf(doc);

        return new SourceSeriesDetail(sourceSeriesId, title, $"{BaseUrl}/manga/{sourceSeriesId}/", cover, description, status);
    }

    private static string? StatusOf(AngleSharp.Dom.IDocument doc)
    {
        foreach (var item in doc.QuerySelectorAll("div.post-content_item"))
        {
            var label = item.QuerySelector(".summary-heading h5")?.TextContent.Trim();
            if (!string.Equals(label, "Status", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = item.QuerySelector(".summary-content")?.TextContent.Trim();
            return value switch
            {
                not null when value.Contains("Andamento", StringComparison.OrdinalIgnoreCase) => "Ongoing",
                not null when value.Contains("Conclu", StringComparison.OrdinalIgnoreCase) ||
                              value.Contains("Complet", StringComparison.OrdinalIgnoreCase) => "Completed",
                _ => value
            };
        }

        return null;
    }

    // ── Chapters ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var doc = await GetHtmlAsync($"manga/{sourceSeriesId}/", ct);

        var chapters = new List<SourceChapter>();
        foreach (var box in doc.QuerySelectorAll("div.chapter-box"))
        {
            var link = box.QuerySelector("a");
            var href = link?.GetAttribute("href");
            if (href is null || !Uri.TryCreate(href, UriKind.Absolute, out var uri))
            {
                continue;
            }

            var chapterSlug = SourceUrl.PathTail(uri, BaseUrl, $"/manga/{sourceSeriesId}/", firstSegmentOnly: true);
            var slugMatch = chapterSlug is null ? null : ChapterSlugRegex().Match(chapterSlug);
            if (chapterSlug is null || slugMatch is not { Success: true })
            {
                continue;
            }

            decimal? number = slugMatch.Groups[2].Success
                ? decimal.Parse($"{slugMatch.Groups[1].Value}.{slugMatch.Groups[2].Value}", CultureInfo.InvariantCulture)
                : decimal.Parse(slugMatch.Groups[1].Value, CultureInfo.InvariantCulture);

            var label = link!.TextContent.Trim();
            var dateText = box.QuerySelector(".chapter-date")?.TextContent.Trim();
            var releaseDate = ParsePortugueseDate(dateText);

            chapters.Add(new SourceChapter(
                Name,
                sourceSeriesId,
                chapterSlug,
                label,
                number,
                Volume: null,
                Title: null,
                Language: "pt-BR",
                releaseDate,
                Url: href));
        }

        return SourceChapterList.Normalize(chapters);
    }

    private static DateTime? ParsePortugueseDate(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var match = PortugueseDateRegex().Match(text);
        if (!match.Success || !PortugueseMonths.TryGetValue(match.Groups[2].Value, out var month))
        {
            return null;
        }

        var day = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var year = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        try
        {
            return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    // ── Page images ───────────────────────────────────────────────────

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var doc = await GetHtmlAsync($"manga/{chapter.SourceSeriesId}/{chapter.SourceChapterId}/", ct);

        // Same-origin CDN (wp-content/uploads on this domain) — no special headers needed.
        var pages = doc.QuerySelectorAll("img.wp-manga-chapter-img")
            .Select(img => img.GetAttribute("src")?.Trim())
            .Where(src => !string.IsNullOrEmpty(src))
            .Select(src => new PageRequest(src!))
            .ToList();

        return new ChapterPages(pages);
    }

    private async Task<AngleSharp.Html.Dom.IHtmlDocument> GetHtmlAsync(string path, CancellationToken ct)
    {
        var html = await Client.GetStringAsync(path, ct);
        return await Parser.ParseDocumentAsync(html, ct);
    }
}
