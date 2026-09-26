using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Maki.Core.Http;
using Maki.Core.Parsing;
using Maki.Core.Sources;

namespace Maki.Sources.AnimeSama;

/// <summary>
/// Anime-Sama scraper. French scan aggregator; every request (HTML and the JSON count
/// endpoint alike) goes through <see cref="IHtmlFetcher"/> since the site sits behind
/// Cloudflare. A series id is a slug ("one-piece") or a slug plus the scan panel path a
/// pasted URL named ("one-piece/scan_noir-et-blanc/vf") - a bare slug always means the
/// first panel <c>panneauScan(...)</c> lists on the series page. The chapter list itself
/// lives in a hand-written inline script per scan page (<c>resetListe/creerListe/newSP/
/// newSPF/finirListe</c>), read in <see cref="BuildLabels"/>; positions from that list are
/// what the page-count JSON and the image URLs are keyed by, not the chapter number.
/// </summary>
public class AnimeSamaSource(IHtmlFetcher fetcher) : ISource
{
    private static readonly HtmlParser Parser = new();

    private static readonly Regex PanneauScanPattern =
        new("""panneauScan\("(.+?)"\s*,\s*"(.+?)"\)""", RegexOptions.Compiled);

    // Baked into panneauAnime/panneauScan's own document.write template, so it never shows
    // up as a real DOM element - only AngleSharp's raw script text carries it.
    private static readonly Regex ImgOeuvreSrcPattern =
        new(@"id=""imgOeuvre""[^>]*\ssrc=""([^""]+)""", RegexOptions.Compiled);

    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex LineComment = new(@"//[^\n]*", RegexOptions.Compiled);
    private static readonly Regex ActiveResetPattern = new(@"resetListe\s*\(\s*\)", RegexOptions.Compiled);

    private static readonly Regex CallPattern = new(
        """(?<name>creerListe|newSPF|newSP|finirListe|resetListe)\s*\(\s*(?<args>[^)]*)\)""",
        RegexOptions.Compiled);

    public string Name => "animesama";
    public string DisplayName => "Anime-Sama";

    public string BaseUrl =>
        Environment.GetEnvironmentVariable("MAKI_SOURCE_ANIMESAMA_BASEURL")?.TrimEnd('/') ?? "https://anime-sama.to";

    public SourceCapabilities Capabilities => SourceCapabilities.NeedsFlareSolverr;
    public IReadOnlyList<string> CoverHosts => ["cdn.jsdelivr.net"];
    public IReadOnlyList<string> SupportedLanguages => ["fr"];

    public string? ResolveSeriesIdFromUrl(Uri url)
    {
        var tail = SourceUrl.PathTail(url, BaseUrl, "/catalogue/");
        if (tail is null)
        {
            return null;
        }

        var segments = tail.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length switch
        {
            1 => segments[0],
            3 when segments[1].StartsWith("scan", StringComparison.OrdinalIgnoreCase) => string.Join('/', segments),
            _ => null
        };
    }

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/catalogue/?type[]=Scans&search={Uri.EscapeDataString(title)}&page=1";
        var html = await fetcher.GetHtmlAsync(url, ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var results = new List<SourceSeriesResult>();
        foreach (var card in doc.QuerySelectorAll("#list_catalog > div.catalog-card"))
        {
            var href = card.QuerySelector("a[href]")?.GetAttribute("href");
            var seriesId = SlugFromHref(href);
            var titleText = card.QuerySelector("h2.card-title")?.TextContent.Trim();
            if (seriesId is null || string.IsNullOrEmpty(titleText))
            {
                continue;
            }

            var cover = card.QuerySelector("img.card-image")?.GetAttribute("src");
            results.Add(new SourceSeriesResult(seriesId, titleText, $"{BaseUrl}/catalogue/{seriesId}/", cover));
        }

        return results;
    }

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var slug = Slug(sourceSeriesId);
        var html = await fetcher.GetHtmlAsync($"{BaseUrl}/catalogue/{slug}/", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var title = doc.QuerySelector("div.my-2 h1")?.TextContent.Trim();
        var synopsis = doc.QuerySelector("p#synopsisText")?.TextContent.Trim();
        var cover = ImgOeuvreSrcPattern.Match(html) is { Success: true } imgMatch
            ? imgMatch.Groups[1].Value
            : doc.QuerySelector("meta[property='og:image']")?.GetAttribute("content");
        var status = StatusFrom(doc);

        return new SourceSeriesDetail(
            sourceSeriesId,
            string.IsNullOrEmpty(title) ? slug : title,
            $"{BaseUrl}/catalogue/{sourceSeriesId}/",
            cover,
            string.IsNullOrEmpty(synopsis) ? null : synopsis,
            status);
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var resolvedId = await ResolvePanelAsync(sourceSeriesId, ct);
        var (_, _, labels, scanUrl) = await LoadOeuvreAsync(resolvedId, ct);

        var chapters = new List<SourceChapter>(labels.Count);
        for (var position = 1; position <= labels.Count; position++)
        {
            var label = labels[position - 1];
            var parsed = ChapterNumberParser.Parse(label);
            chapters.Add(new SourceChapter(
                Name,
                sourceSeriesId,
                $"{resolvedId}|{label}",
                label,
                parsed.Number,
                Volume: null,
                // Null-number identity (ChapterIdentity.Matches) is IsOneShot + Language + Title, so
                // every special needs its own Title or "One Shot" and a differently-named special
                // would collide into the same row. Numbered chapters keep Title null.
                Title: parsed.Number is null ? label : null,
                Language: "fr",
                ReleaseDate: null,
                Url: scanUrl));
        }

        return SourceChapterList.Normalize(chapters);
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var separator = chapter.SourceChapterId.LastIndexOf('|');
        if (separator < 0)
        {
            throw new InvalidOperationException($"animesama: malformed chapter id '{chapter.SourceChapterId}'");
        }

        var resolvedId = chapter.SourceChapterId[..separator];
        var label = chapter.SourceChapterId[(separator + 1)..];

        var (oeuvre, counts, labels, _) = await LoadOeuvreAsync(resolvedId, ct);
        var position = labels.IndexOf(label) + 1;
        if (position <= 0)
        {
            throw new InvalidOperationException($"animesama: label '{label}' not found for '{resolvedId}'");
        }

        if (!counts.TryGetValue(position, out var pageCount))
        {
            throw new InvalidOperationException($"animesama: no page count for position {position} of '{resolvedId}'");
        }

        if (pageCount == 0)
        {
            // The count endpoint says this position has no images yet - treat it the same as a
            // paid/early-access lock elsewhere, so the queue retries later instead of writing an
            // empty CBZ.
            throw new ChapterLockedException(
                $"animesama: chapter '{label}' of '{resolvedId}' has 0 pages (position {position})");
        }

        var headers = new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" };
        var pages = new List<PageRequest>(pageCount);
        for (var n = 1; n <= pageCount; n++)
        {
            pages.Add(new PageRequest($"{BaseUrl}/s2/scans/{Uri.EscapeDataString(oeuvre)}/{position}/{n}.jpg", headers));
        }

        return new ChapterPages(pages);
    }

    /// <summary>Resolves a bare slug to "slug/panel" via the series page's first scan panel; a panel already
    /// carried by the id is used as-is.</summary>
    private async Task<string> ResolvePanelAsync(string sourceSeriesId, CancellationToken ct)
    {
        var segments = sourceSeriesId.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 3)
        {
            return sourceSeriesId;
        }

        var slug = segments[0];
        var html = await fetcher.GetHtmlAsync($"{BaseUrl}/catalogue/{slug}/", ct);
        var panel = FirstScanPanel(html) ??
            throw new InvalidOperationException($"animesama: no scan panel found for '{slug}'");

        return $"{slug}/{panel}";
    }

    /// <summary>Fetches the scan page and page-count JSON for a resolved "slug/panel" id and builds the
    /// position-to-label list. Two requests, same shape whether called for listing or for pages.</summary>
    private async Task<(string Oeuvre, IReadOnlyDictionary<int, int> Counts, List<string> Labels, string ScanUrl)>
        LoadOeuvreAsync(string resolvedId, CancellationToken ct)
    {
        var scanUrl = $"{BaseUrl}/catalogue/{resolvedId}/";
        var html = await fetcher.GetHtmlAsync(scanUrl, ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var oeuvre = doc.QuerySelector("#titreOeuvre")?.TextContent.Trim();
        if (string.IsNullOrEmpty(oeuvre))
        {
            throw new InvalidOperationException($"animesama: no #titreOeuvre on {scanUrl}");
        }

        var counts = await FetchCountsAsync(oeuvre, ct);
        var labels = BuildLabels(doc, counts.Count);
        return (oeuvre, counts, labels, scanUrl);
    }

    /// <summary>
    /// Fetches the page-count JSON for an oeuvre name, retrying once with its '&amp;'-escaped form
    /// (the site's own script reads the name off innerHTML, which re-escapes an ampersand where our
    /// decoded #titreOeuvre text does not) before giving up.
    /// </summary>
    private async Task<Dictionary<int, int>> FetchCountsAsync(string oeuvre, CancellationToken ct)
    {
        var counts = await TryFetchCountsAsync(oeuvre, ct);
        if (counts is not null)
        {
            return counts;
        }

        if (oeuvre.Contains('&'))
        {
            counts = await TryFetchCountsAsync(oeuvre.Replace("&", "&amp;"), ct);
            if (counts is not null)
            {
                return counts;
            }
        }

        throw new InvalidOperationException($"animesama: oeuvre '{oeuvre}' not found");
    }

    /// <summary>Null means the count endpoint answered its "not found" JSON rather than counts.</summary>
    private async Task<Dictionary<int, int>?> TryFetchCountsAsync(string oeuvre, CancellationToken ct)
    {
        var countUrl = $"{BaseUrl}/s2/scans/get_nb_chap_et_img.php?oeuvre={Uri.EscapeDataString(oeuvre)}";
        var countBody = UnwrapJson(await fetcher.GetHtmlAsync(countUrl, ct));

        using var countsJson = System.Text.Json.JsonDocument.Parse(countBody);
        if (countsJson.RootElement.TryGetProperty("error", out _))
        {
            return null;
        }

        var counts = new Dictionary<int, int>();
        foreach (var property in countsJson.RootElement.EnumerateObject())
        {
            if (int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var position))
            {
                counts[position] = property.Value.GetInt32();
            }
        }

        return counts;
    }

    /// <summary>
    /// Builds the position-to-label list the way scans.js does: find the script that actually calls
    /// <c>resetListe()</c> once its <c>/* ... */</c> template comment is stripped (the comment alone
    /// contains that same call, uninstantiated, and would otherwise look like the real thing), then
    /// walk creerListe/newSP/newSPF/finirListe in order. No such script (a plain panel like One Piece
    /// Couleur) means labels are 1..total.
    /// </summary>
    internal static List<string> BuildLabels(IDocument doc, int total)
    {
        List<string>? labels = null;
        foreach (var script in doc.QuerySelectorAll("script"))
        {
            var stripped = StripComments(script.TextContent);
            if (!ActiveResetPattern.IsMatch(stripped))
            {
                continue;
            }

            labels = BuildLabelsFromCalls(stripped, total);
            break;
        }

        labels ??= Enumerable.Range(1, total).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList();

        if (labels.Count != total)
        {
            // Either a call this parser doesn't know about skewed the count, or the site's total
            // (the page-count JSON's key count) disagrees with the script - either way, guessing
            // which position anything landed on would silently mislabel real chapters.
            throw new InvalidOperationException(
                $"animesama: scan-list script produced {labels.Count} labels, expected {total}");
        }

        return labels;
    }

    private static List<string> BuildLabelsFromCalls(string stripped, int total)
    {
        var labels = new List<string>();
        var delay = 0;

        foreach (Match match in CallPattern.Matches(stripped))
        {
            var name = match.Groups["name"].Value;
            var args = match.Groups["args"].Value.Trim();

            switch (name)
            {
                case "resetListe":
                    break;

                case "creerListe":
                {
                    var parts = args.Split(',');
                    var a = int.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
                    var b = int.Parse(parts[1].Trim(), CultureInfo.InvariantCulture);
                    for (var i = a; i <= b; i++)
                    {
                        labels.Add(i.ToString(CultureInfo.InvariantCulture));
                    }

                    break;
                }

                case "newSP":
                case "newSPF":
                    labels.Add(args.Trim('"'));
                    delay++;
                    break;

                case "finirListe":
                {
                    var n = int.Parse(args, CultureInfo.InvariantCulture);
                    var end = total - delay;
                    for (var i = n; i <= end; i++)
                    {
                        labels.Add(i.ToString(CultureInfo.InvariantCulture));
                    }

                    break;
                }
            }
        }

        return labels;
    }

    private static string StripComments(string js) => LineComment.Replace(BlockComment.Replace(js, string.Empty), string.Empty);

    /// <summary>Skips the commented-out usage template (literal args "nom"/"url") and any unverified
    /// translation path (Keiyoushi's own rule, kept here for parity).</summary>
    internal static string? FirstScanPanel(string html)
    {
        foreach (Match match in PanneauScanPattern.Matches(html))
        {
            var label = match.Groups[1].Value;
            var path = match.Groups[2].Value;
            if (label == "nom" && path == "url")
            {
                continue;
            }

            if (path.Contains("/va", StringComparison.Ordinal))
            {
                continue;
            }

            return path;
        }

        return null;
    }

    private static string? StatusFrom(IDocument doc)
    {
        foreach (var label in doc.QuerySelectorAll("span.info-lbl"))
        {
            if (label.TextContent.Contains("État", StringComparison.Ordinal))
            {
                return label.NextElementSibling?.TextContent.Trim();
            }
        }

        return null;
    }

    private static string Slug(string sourceSeriesId) => sourceSeriesId.Split('/', 2)[0];

    private static string? SlugFromHref(string? href)
    {
        if (string.IsNullOrEmpty(href))
        {
            return null;
        }

        var path = Uri.TryCreate(href, UriKind.Absolute, out var uri) ? uri.AbsolutePath : href;
        const string marker = "/catalogue/";
        var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var tail = path[(index + marker.Length)..].Trim('/');
        return tail.Length == 0 ? null : tail.Split('/')[0];
    }

    /// <summary>FlareSolverr wraps a JSON response body in a browser-rendered &lt;pre&gt;; a direct
    /// (cached-clearance) fetch does not, so only unwrap when it looks like markup.</summary>
    private static string UnwrapJson(string body)
    {
        var trimmed = body.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '<')
        {
            return body;
        }

        var doc = Parser.ParseDocument(trimmed);
        return doc.QuerySelector("pre")?.TextContent ?? body;
    }
}
