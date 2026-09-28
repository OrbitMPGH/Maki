using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Maki.Core.Http;
using Maki.Core.Parsing;
using Maki.Core.Sources;

namespace Maki.Sources.Manhwa18Net;

/// <summary>
/// Manhwa18.net scraper. Adult Korean manhwa, official English translations plus separate
/// Korean raw-upload series (a raw's <c>genres</c> list carries <c>Raw</c> instead of
/// <c>Adult</c>/<c>Manhwa</c>/<c>Mature</c>, and its slug/name end in <c>-raw</c>). Raws are
/// dropped from search results and, when a raw series is linked directly by a pasted URL, every
/// chapter is tagged <c>ko</c> instead of the default <c>en</c>.
/// <para>
/// The site is a Laravel + Inertia.js app: every page renders its full server props as an
/// HTML-entity-encoded JSON object on <c>div#app[data-page]</c>. AngleSharp decodes the entities
/// when the attribute is read, so the value is parsed with <see cref="JsonDocument"/> directly.
/// A missing <c>#app</c> or attribute throws rather than reading as "no chapters", since that
/// shape is what a Cloudflare interstitial would produce. Cloudflare is passive today but the
/// site still carries the capability so a future challenge is handled rather than 403ing.
/// </para>
/// <para>
/// Series id is the slug (<c>secret-class</c>); chapter id is the chapter's own slug
/// (<c>chapter-318</c>, or an older <c>chap-01-361</c> whose numeric suffix has nothing to do
/// with the chapter number, so it is never rebuilt from one).
/// </para>
/// </summary>
public partial class Manhwa18NetSource(IHtmlFetcher fetcher) : ISource
{
    private static readonly HtmlParser Parser = new();

    public string Name => "manhwa18net";
    public string DisplayName => "Manhwa18.net";
    public string BaseUrl => "https://manhwa18.net";
    public SourceCapabilities Capabilities => SourceCapabilities.NeedsFlareSolverr;
    public SourceContent Content => SourceContent.Manhwa;
    public SourceRating Rating => SourceRating.Adult;

    // "Chap 01" / "chap 113" fall outside ChapterNumberParser's ch/chapter word-boundary
    // pattern (it only matches "ch" or "chapter"), so the number is pre-extracted here and
    // handed to the parser as a bare digit string.
    [GeneratedRegex(@"^(?:chap(?:ter)?)\.?\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex ChapterPrefixPattern();

    // "Secret class raw" / "Foo (Raw)" but not "Quick Draw" or "The Last Straw".
    [GeneratedRegex(@"\braw\b\W*$", RegexOptions.IgnoreCase)]
    private static partial Regex RawNameSuffixPattern();

    public string? ResolveSeriesIdFromUrl(Uri url)
    {
        // https://manhwa18.net/manga/{slug} — a chapter URL adds a second segment and must
        // not resolve to its series (SourceUrl.PathTail alone can't tell the two apart).
        var tail = SourceUrl.PathTail(url, BaseUrl, "/manga/");
        return tail is not null && !tail.Contains('/') ? tail : null;
    }

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var props = await GetPagePropsAsync($"{BaseUrl}/tim-kiem?q={Uri.EscapeDataString(title)}", ct);
        if (!props.TryGetProperty("mangas", out var mangas) ||
            !mangas.TryGetProperty("data", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<SourceSeriesResult>();
        foreach (var item in rows.EnumerateArray())
        {
            var slug = String(item, "slug");
            var name = String(item, "name");
            if (string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(name))
            {
                continue;
            }

            // Search hits carry no genres to check, so the -raw slug/name suffix is the only
            // signal available here; the fuller genre check runs in ListChaptersAsync instead.
            if (slug.EndsWith("-raw", StringComparison.OrdinalIgnoreCase) ||
                RawNameSuffixPattern().IsMatch(name))
            {
                continue;
            }

            results.Add(new SourceSeriesResult(
                slug, name, $"{BaseUrl}/manga/{slug}", String(item, "cover_url"), PlainText(String(item, "pilot"))));
        }

        return results;
    }

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var props = await GetPagePropsAsync($"{BaseUrl}/manga/{sourceSeriesId}", ct);
        if (!props.TryGetProperty("manga", out var manga))
        {
            throw new InvalidOperationException($"Manhwa18.net returned no manga props for {sourceSeriesId}");
        }

        // description is empty on every title observed so far; pilot (the search synopsis) is
        // the fallback rather than the other way round.
        var description = String(manga, "description");
        description = string.IsNullOrWhiteSpace(description) ? String(manga, "pilot") : description;

        return new SourceSeriesDetail(
            sourceSeriesId,
            String(manga, "name") ?? sourceSeriesId,
            $"{BaseUrl}/manga/{sourceSeriesId}",
            String(manga, "cover_url"),
            PlainText(description));
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var props = await GetPagePropsAsync($"{BaseUrl}/manga/{sourceSeriesId}", ct);
        if (!props.TryGetProperty("manga", out var manga) ||
            !props.TryGetProperty("chapters", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        // A raw series' genres carry "Raw" instead of "Adult"/"Manhwa"/"Mature"/…; this only
        // matters for a raw series linked directly by a pasted URL (SearchAsync already drops
        // raws by name), and it stops Korean pages being filed as an English chapter.
        var isRaw = manga.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array &&
                    genres.EnumerateArray().Any(g =>
                        string.Equals(String(g, "name"), "Raw", StringComparison.OrdinalIgnoreCase));
        var language = isRaw ? "ko" : "en";

        var chapters = new List<(SourceChapter Chapter, string Label)>();
        foreach (var row in rows.EnumerateArray())
        {
            var slug = String(row, "slug");
            var label = String(row, "name");
            if (string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(label))
            {
                continue;
            }

            var prefixMatch = ChapterPrefixPattern().Match(label);
            var parsed = prefixMatch.Success
                ? ChapterNumberParser.Parse(prefixMatch.Groups[1].Value)
                : ChapterNumberParser.Parse(label);

            chapters.Add((new SourceChapter(
                Name,
                sourceSeriesId,
                slug,
                label,
                parsed.Number,
                parsed.Volume,
                // Null-number chapter identity is IsOneShot + Language + Title, so the raw name
                // has to ride along as Title or the row collapses into every other unparsed one.
                Title: parsed.Number is null ? label : null,
                language,
                ReleaseDate(row),
                Url: $"{BaseUrl}/manga/{sourceSeriesId}/{slug}"), label));
        }

        // The site lists newest first; Normalize sorts ascending. "Chapter 316" and "Chapter 316
        // Uncensored" are the same number under two names — prefer the uncensored copy.
        return SourceChapterList.Normalize(
            chapters,
            c => c.Chapter,
            group => group.OrderByDescending(c => c.Label.Contains("Uncensored", StringComparison.OrdinalIgnoreCase))
                .First());
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var props = await GetPagePropsAsync(
            $"{BaseUrl}/manga/{chapter.SourceSeriesId}/{chapter.SourceChapterId}", ct);

        // Sent on every page request: both CDNs answered fine without one in the live probe, but
        // Keiyoushi's extension sends the site Referer on image requests, so this does too.
        var headers = new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" };

        var pages = new List<PageRequest>();
        if (props.TryGetProperty("chapterImages", out var images) && images.ValueKind == JsonValueKind.Array)
        {
            foreach (var image in images.EnumerateArray())
            {
                var src = String(image, "src");
                if (!string.IsNullOrEmpty(src))
                {
                    pages.Add(new PageRequest(src, headers));
                }
            }
        }

        if (pages.Count == 0 && props.TryGetProperty("chapterContent", out var contentEl) &&
            contentEl.ValueKind == JsonValueKind.String)
        {
            // Fallback for when chapterImages is absent: the same list rendered as <img> HTML.
            // src first, then data-src, then data-lazy-src, mirroring the Keiyoushi extension.
            var doc = await Parser.ParseDocumentAsync(contentEl.GetString() ?? string.Empty, ct);
            foreach (var img in doc.QuerySelectorAll("img"))
            {
                var src = img.GetAttribute("src") ?? img.GetAttribute("data-src") ?? img.GetAttribute("data-lazy-src");
                if (!string.IsNullOrEmpty(src))
                {
                    pages.Add(new PageRequest(src, headers));
                }
            }
        }

        if (pages.Count == 0)
        {
            throw new ChapterLockedException(
                $"Manhwa18.net chapter {chapter.SourceChapterId} has no pages (locked or not yet public).");
        }

        return new ChapterPages(pages);
    }

    /// <summary>
    /// The <c>props</c> object of an Inertia page, read out of <c>div#app[data-page]</c>.
    /// Throws rather than returning an empty shape when it's missing, since that's what a
    /// Cloudflare interstitial would look like and must not read as "no chapters".
    /// </summary>
    private async Task<JsonElement> GetPagePropsAsync(string url, CancellationToken ct)
    {
        var html = await fetcher.GetHtmlAsync(url, ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);
        var raw = doc.QuerySelector("div#app")?.GetAttribute("data-page");
        if (string.IsNullOrEmpty(raw))
        {
            throw new InvalidOperationException($"Manhwa18.net returned no #app data-page for {url}");
        }

        using var json = JsonDocument.Parse(raw);
        return json.RootElement.TryGetProperty("props", out var props) ? props.Clone() : default;
    }

    private static DateTime? ReleaseDate(JsonElement row) =>
        row.TryGetProperty("created_at", out var value) && value.ValueKind == JsonValueKind.String &&
        DateTime.TryParse(
            value.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    /// <summary>Reads a property as a string whether the site stored it as one or as a number.</summary>
    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;

    /// <summary>Synopses are stored as rendered HTML (a single &lt;p&gt; in practice).</summary>
    private static string? PlainText(string? html) =>
        string.IsNullOrWhiteSpace(html) ? null : Parser.ParseDocument(html).Body?.TextContent.Trim();
}
