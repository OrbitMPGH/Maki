using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using Maki.Core.Parsing;
using Maki.Core.Sources;

namespace Maki.Sources.Dynasty;

/// <summary>
/// Dynasty Scans (yuri manga and doujinshi aggregator). Every permalink page has a JSON
/// twin at the same path plus ".json"; we use those instead of scraping markup, except
/// for search, which has no JSON endpoint ("/search.json" 500s). Series id and chapter id
/// are both the site's own permalink (e.g. "citrus", "citrus_ch01"). Only entries of
/// type "Series" are supported: doujins, anthologies and issues are collections of
/// unrelated one-shots that don't map to one MangaBaka entry.
/// </summary>
public partial class DynastySource(IHttpClientFactory httpClientFactory) : ISource
{
    public const string HttpClientName = "source-dynasty";

    private static readonly HtmlParser Parser = new();

    public string Name => "dynasty";
    public string DisplayName => "Dynasty Scans";
    public string BaseUrl => "https://dynasty-scans.com";
    public SourceCapabilities Capabilities => SourceCapabilities.None;
    public IReadOnlyList<string> CoverHosts => [];

    private HttpClient Client => httpClientFactory.CreateClient(HttpClientName);

    [GeneratedRegex(@"^Volume\s+(\d+)(?!\d|\.\d)", RegexOptions.IgnoreCase)]
    private static partial Regex VolumeHeaderPattern();

    public string? ResolveSeriesIdFromUrl(Uri url)
    {
        var tail = SourceUrl.PathTail(url, BaseUrl, "/series/", firstSegmentOnly: true);
        if (tail is null)
        {
            return null;
        }

        return tail.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? tail[..^".json".Length] : tail;
    }

    public async Task<IReadOnlyList<SourceSeriesResult>> SearchAsync(string title, CancellationToken ct = default)
    {
        var html = await Client.GetStringAsync($"search?q={Uri.EscapeDataString(title)}&classes%5B%5D=Series", ct);
        var doc = await Parser.ParseDocumentAsync(html, ct);

        var results = new List<SourceSeriesResult>();
        foreach (var link in doc.QuerySelectorAll("dl.chapter-list dd a.name[href^='/series/']"))
        {
            var href = link.GetAttribute("href");
            if (string.IsNullOrEmpty(href))
            {
                continue;
            }

            var name = link.TextContent.Trim();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            // No covers in search results; SearchController's own detail lookup fills one in.
            results.Add(new SourceSeriesResult(href["/series/".Length..], name, $"{BaseUrl}{href}"));
        }

        return results;
    }

    public async Task<SourceSeriesDetail> GetSeriesAsync(string sourceSeriesId, CancellationToken ct = default)
    {
        var root = await GetJsonAsync($"series/{sourceSeriesId}.json", ct);
        RequireSeries(sourceSeriesId, root);

        var name = String(root, "name") ?? sourceSeriesId;
        var cover = String(root, "cover");
        var description = PlainText(String(root, "description"));

        return new SourceSeriesDetail(
            sourceSeriesId,
            name,
            $"{BaseUrl}/series/{sourceSeriesId}",
            string.IsNullOrEmpty(cover) ? null : $"{BaseUrl}{cover}",
            description,
            StatusTag(root));
    }

    public async Task<IReadOnlyList<SourceChapter>> ListChaptersAsync(
        string sourceSeriesId, string? languageFilter = null, CancellationToken ct = default)
    {
        var root = await GetJsonAsync($"series/{sourceSeriesId}.json", ct);
        RequireSeries(sourceSeriesId, root);

        var taggings = new List<JsonElement>();
        if (root.TryGetProperty("taggings", out var initial) && initial.ValueKind == JsonValueKind.Array)
        {
            taggings.AddRange(initial.EnumerateArray());
        }

        // Only large doujin/anthology tags paginate; a plain series rarely does, but honour
        // total_pages when it shows up rather than silently truncating the chapter list.
        if (root.TryGetProperty("total_pages", out var totalPagesEl) && totalPagesEl.ValueKind == JsonValueKind.Number)
        {
            var totalPages = totalPagesEl.GetInt32();
            for (var page = 2; page <= totalPages; page++)
            {
                var pageRoot = await GetJsonAsync($"series/{sourceSeriesId}.json?page={page}", ct);
                if (pageRoot.TryGetProperty("taggings", out var more) && more.ValueKind == JsonValueKind.Array)
                {
                    taggings.AddRange(more.EnumerateArray());
                }
            }
        }

        var chapters = new List<SourceChapter>();
        int? volume = null;
        foreach (var tagging in taggings)
        {
            if (tagging.TryGetProperty("header", out var headerEl) && headerEl.ValueKind == JsonValueKind.String)
            {
                // A header applies to every chapter after it until the next one; "Extra", "Volume 10.5"
                // or any other header that isn't a whole "Volume N" resets the running volume to null.
                var match = VolumeHeaderPattern().Match(headerEl.GetString() ?? string.Empty);
                volume = match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
                continue;
            }

            var permalink = String(tagging, "permalink");
            if (string.IsNullOrEmpty(permalink))
            {
                continue;
            }

            var rawTitle = String(tagging, "title");
            var parsed = ChapterNumberParser.Parse(rawTitle, volume?.ToString(CultureInfo.InvariantCulture));

            // ChapterIdentity.Matches identifies a null-number chapter by (IsOneShot, Language,
            // Title) alone, ignoring Volume entirely. A null Title here would make every
            // colon-less special ("Vol. 1 Special", "Vol. 7 Extra", ...) match on sync no matter
            // which volume it came from, collapsing them all into one chapter row after the
            // first. Numbered chapters aren't looked up by Title, so they keep the null fallback.
            var colonIndex = rawTitle?.IndexOf(": ", StringComparison.Ordinal) ?? -1;
            var chapterTitle = colonIndex >= 0 ? rawTitle![(colonIndex + 2)..]
                : parsed.Number is null ? rawTitle
                : null;

            chapters.Add(new SourceChapter(
                Name,
                sourceSeriesId,
                permalink,
                rawTitle,
                parsed.Number,
                parsed.Volume,
                chapterTitle,
                Language: "en",
                ReleaseDate: ReleasedOn(tagging),
                Url: $"{BaseUrl}/chapters/{permalink}"));
        }

        return SourceChapterList.Normalize(chapters);
    }

    public async Task<ChapterPages> GetPagesAsync(SourceChapter chapter, CancellationToken ct = default)
    {
        var root = await GetJsonAsync($"chapters/{chapter.SourceChapterId}.json", ct);
        var headers = new Dictionary<string, string> { ["Referer"] = $"{BaseUrl}/" };

        var pages = new List<PageRequest>();
        if (root.TryGetProperty("pages", out var pageArray) && pageArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var page in pageArray.EnumerateArray())
            {
                var url = String(page, "url");
                if (!string.IsNullOrEmpty(url))
                {
                    pages.Add(new PageRequest($"{BaseUrl}{url}", headers));
                }
            }
        }

        return new ChapterPages(pages);
    }

    private static void RequireSeries(string sourceSeriesId, JsonElement root)
    {
        var type = String(root, "type");
        if (!string.Equals(type, "Series", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Dynasty Scans entry '{sourceSeriesId}' is a {type ?? "unknown type"}, not a Series");
        }
    }

    private static DateTime? ReleasedOn(JsonElement tagging)
    {
        var text = String(tagging, "released_on");
        return text is not null &&
            DateTime.TryParseExact(
                text, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date
            : null;
    }

    private static string? StatusTag(JsonElement root)
    {
        if (!root.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var tag in tags.EnumerateArray())
        {
            if (string.Equals(String(tag, "type"), "Status", StringComparison.Ordinal))
            {
                return String(tag, "name");
            }
        }

        return null;
    }

    /// <summary>Descriptions are stored as rendered HTML, tags and all.</summary>
    private static string? PlainText(string? html) =>
        string.IsNullOrWhiteSpace(html) ? null : Parser.ParseDocument(html).Body?.TextContent.Trim();

    private static string? String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private async Task<JsonElement> GetJsonAsync(string path, CancellationToken ct)
    {
        var body = await Client.GetStringAsync(path, ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }
}
