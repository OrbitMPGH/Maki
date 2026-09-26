using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Maki.Core.Http;
using Maki.Core.Parsing;
using Maki.Core.Sources;

namespace Maki.Sources.Toonily;

/// <summary>
/// Toonily scraper (Madara WordPress theme, adult Korean manhwa, English). Passive Cloudflare on
/// 2026-09-26 (every probed page answered 200 to a plain client), so every GET goes through
/// <see cref="IHtmlFetcher"/> (direct with cached clearance first, FlareSolverr on a miss). Search
/// is a POST to Madara's admin-ajax endpoint, sent through <see cref="IHtmlFetcher.FetchAsync"/>
/// with the <c>toonily-mature</c> cookie so mature titles show up on either path (direct or
/// FlareSolverr). The GET search is kept only as a fallback if that POST ever throws, carrying the
/// same cookie. Series id is the slug path segment (<c>secret-class-38c3e37a</c>); chapter id is
/// the chapter slug (<c>chapter-242</c>).
/// </summary>
public partial class ToonilySource(IHtmlFetcher fetcher) : ISource
{
    private static readonly HtmlParser Parser = new();

    /// <summary>Opts search into mature titles; series and chapter pages serve them without it.</summary>
    private static readonly IReadOnlyDictionary<string, string> MatureCookie =
        new Dictionary<string, string> { ["toonily-mature"] = "1" };

    public string Name => "toonily";
    public string DisplayName => "Toonily";
    public string BaseUrl => "https://toonily.com";
    public SourceCapabilities Capabilities => SourceCapabilities.NeedsFlareSolverr;

    /// <summary>Covers on static.tnlycdn.com, page images on data.tnlycdn.com.</summary>
    public IReadOnlyList<string> CoverHosts => ["tnlycdn.com"];

    [GeneratedRegex(@"^(\d+)\s+(minute|minutes|hour|hours|day|days|week|weeks|month|months|year|years)\s+ago$",
        RegexOptions.IgnoreCase)]
    private static partial Regex RelativeDatePattern();

    public string? ResolveSeriesIdFromUrl(Uri url)
    {
        var tail = SourceUrl.PathTail(url, BaseUrl, "/serie/") ?? SourceUrl.PathTail(url, BaseUrl, "/webtoon/");
        return tail is not null && !tail.Contains('/') ? tail : null;
    }

    // ── Search ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        try
        {
            return await SearchViaAjaxAsync(title, ct);
        }
        catch (HttpRequestException)
        {
            // The POST endpoint threw (FlareSolverr itself unreachable, or a raw HTTP-level
            // failure). Fall back to the GET search, carrying the same mature cookie.
            return await SearchViaFallbackAsync(title, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The fetcher client's own Timeout fired on the POST, not the caller cancelling.
            // The fetcher lets that escape without trying FlareSolverr, so fall back here too.
            return await SearchViaFallbackAsync(title, ct);
        }
    }

    private async Task<IReadOnlyList<SourceSeriesResult>> SearchViaAjaxAsync(string title, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["action"] = "madara_load_more",
            ["page"] = "0",
            ["template"] = "madara-core/content/content-archive",
            ["vars[paged]"] = "1",
            ["vars[template]"] = "archive",
            ["vars[posts_per_page]"] = "25",
            ["vars[post_type]"] = "wp-manga",
            ["vars[post_status]"] = "publish",
            ["vars[manga_archives_item_layout]"] = "big_thumbnail",
            ["vars[s]"] = title
        };

        var html = await fetcher.FetchAsync(
            new HtmlFetchRequest($"{BaseUrl}/wp-admin/admin-ajax.php", MatureCookie, BuildFormBody(form)), ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);
        return ToResults(MadaraParser.ParseArchive(doc));
    }

    private async Task<IReadOnlyList<SourceSeriesResult>> SearchViaFallbackAsync(string title, CancellationToken ct)
    {
        var slug = SearchSlug(title);
        if (slug.Length == 0)
        {
            return [];
        }

        var html = await fetcher.FetchAsync(new HtmlFetchRequest($"{BaseUrl}/search/{slug}", MatureCookie), ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);
        return ToResults(MadaraParser.ParseArchive(doc));
    }

    /// <summary>application/x-www-form-urlencoded body (the only kind FlareSolverr's POST accepts).</summary>
    private static string BuildFormBody(IReadOnlyDictionary<string, string> form) =>
        string.Join("&", form.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

    /// <summary>Title lowercased with every run of non-[a-z0-9] collapsed to a single hyphen.</summary>
    internal static string SearchSlug(string title)
    {
        var slug = new StringBuilder(title.Length);
        foreach (var c in title.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        return slug.ToString().Trim('-');
    }

    private IReadOnlyList<SourceSeriesResult> ToResults(IReadOnlyList<MadaraParser.ArchiveItem> items)
    {
        var results = new List<SourceSeriesResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var seriesId = SeriesIdFromHref(item.Href);
            if (seriesId is null || !seen.Add(seriesId))
            {
                continue;
            }

            results.Add(new SourceSeriesResult(seriesId, item.Title, $"{BaseUrl}/serie/{seriesId}/", item.CoverUrl));
        }

        return results;
    }

    // ── Series detail ─────────────────────────────────────────────────

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var seriesId = NormalizeSeriesId(sourceSeriesId);
        var html = await fetcher.GetHtmlAsync($"{BaseUrl}/serie/{seriesId}/", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var info = MadaraParser.ParseSeries(doc);
        return new SourceSeriesDetail(
            seriesId,
            info.Title ?? seriesId,
            $"{BaseUrl}/serie/{seriesId}/",
            info.CoverUrl,
            info.Description,
            MapStatus(info.Status));
    }

    /// <summary>"OnGoing" -> Ongoing, "Completed"/"Ended" -> Completed, anything else -> null.</summary>
    private static string? MapStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (raw.Contains("ongoing", StringComparison.OrdinalIgnoreCase))
        {
            return "Ongoing";
        }

        if (raw.Contains("complete", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("end", StringComparison.OrdinalIgnoreCase))
        {
            return "Completed";
        }

        return null;
    }

    // ── Chapters ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var seriesId = NormalizeSeriesId(sourceSeriesId);

        // The chapter list is server-rendered into the same document as the series page (Toonily's
        // ajax chapter endpoint is a POST, which the fetcher cannot make), so no extra request.
        var html = await fetcher.GetHtmlAsync($"{BaseUrl}/serie/{seriesId}/", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var chapters = new List<SourceChapter>();
        foreach (var item in MadaraParser.ParseChapters(doc))
        {
            var chapter = ToChapter(seriesId, item);
            if (chapter is not null)
            {
                chapters.Add(chapter);
            }
        }

        // Site lists newest first.
        return SourceChapterList.Normalize(chapters);
    }

    private SourceChapter? ToChapter(string seriesId, MadaraParser.ChapterItem item)
    {
        var chapterId = SourceUrl.PathTail(
            new Uri(item.Href), BaseUrl, $"/serie/{seriesId}/", firstSegmentOnly: true);
        if (chapterId is null)
        {
            return null;
        }

        var parsed = ChapterNumberParser.Parse(item.Name, item.VolumeRaw);

        // An unnumbered chapter's identity is IsOneShot + Language + Title (Normalize keys on
        // Number/Volume/Language), so two differently-named unnumbered entries need distinct
        // Titles or they alias each other. A numbered chapter's name carries nothing beyond the
        // number worth keeping, so Title stays null there.
        var title = parsed.Number is null ? item.Name : null;

        return new SourceChapter(
            Name,
            seriesId,
            chapterId,
            item.Name,
            parsed.Number,
            parsed.Volume,
            title,
            Language: "en",
            ParseReleaseDate(item.DateText),
            Url: item.Href);
    }

    /// <summary>"Dec 9, 24" (MMM d, yy, UTC) or relative text ("2 hours ago"); null if neither parses.</summary>
    private static DateTime? ParseReleaseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();

        if (DateTime.TryParseExact(
                text, "MMM d, yy", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var absolute))
        {
            return absolute;
        }

        var match = RelativeDatePattern().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var amount = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        TimeSpan? delta = match.Groups[2].Value.ToLowerInvariant() switch
        {
            "minute" or "minutes" => TimeSpan.FromMinutes(amount),
            "hour" or "hours" => TimeSpan.FromHours(amount),
            "day" or "days" => TimeSpan.FromDays(amount),
            "week" or "weeks" => TimeSpan.FromDays(amount * 7),
            "month" or "months" => TimeSpan.FromDays(amount * 30),
            "year" or "years" => TimeSpan.FromDays(amount * 365),
            _ => null
        };

        return delta is null ? null : DateTime.UtcNow - delta.Value;
    }

    // ── Page images ───────────────────────────────────────────────────

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var html = await fetcher.GetHtmlAsync(
            $"{BaseUrl}/serie/{chapter.SourceSeriesId}/{chapter.SourceChapterId}/", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        // The CDN 403s a page request with no Referer.
        var headers = new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" };

        var pages = new List<PageRequest>();
        foreach (var img in MadaraParser.ParsePages(doc))
        {
            var url = MadaraParser.ImageUrl(img);
            if (string.IsNullOrWhiteSpace(url) || IsPromoImage(img, url))
            {
                continue;
            }

            pages.Add(new PageRequest(url, headers));
        }

        if (pages.Count == 0)
        {
            throw new ChapterLockedException(
                $"Toonily chapter {chapter.SourceChapterId} has no pages (locked or not yet public).");
        }

        return new ChapterPages(pages);
    }

    /// <summary>Drops Toonily's own "Discord Server" promo tile from the page list.</summary>
    private static bool IsPromoImage(IElement img, string url) =>
        string.Equals(img.GetAttribute("id"), "image-999", StringComparison.Ordinal) ||
        url.Contains("/wp-content/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Accepts a bare slug or a full series URL, since older mappings stored the URL.</summary>
    private string NormalizeSeriesId(string id)
    {
        if (!id.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return id.Trim('/');
        }

        return Uri.TryCreate(id, UriKind.Absolute, out var uri) ? ResolveSeriesIdFromUrl(uri) ?? id : id;
    }

    private string? SeriesIdFromHref(string href) =>
        Uri.TryCreate(href, UriKind.Absolute, out var uri) ? ResolveSeriesIdFromUrl(uri) : null;
}
