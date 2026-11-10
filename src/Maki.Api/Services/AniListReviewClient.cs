using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Maki.Api.Services;

/// <summary>
/// The most upvoted AniList reviews for a series, from AniList's public GraphQL API (no token).
/// Looks the series up by AniList id, or by MAL id when that is all the caller has, since AniList
/// indexes both. Same contract as <see cref="MalReviewClient"/>: null is a failed fetch, an empty
/// list is a series nobody reviewed (or one AniList doesn't know), and only successes are cached.
/// </summary>
public partial class AniListReviewClient(IHttpClientFactory httpClientFactory, ILogger<AniListReviewClient> logger)
{
    public const string HttpClientName = "anilist-reviews";

    private const int MaxReviews = 3;

    // A few spares, because a review that is all spoiler blocks cleans down to nothing.
    private const string Query = """
        query ($id: Int, $idMal: Int) {
          Media(id: $id, idMal: $idMal, type: MANGA) {
            reviews(sort: [RATING_DESC], perPage: 6) {
              nodes { summary body score rating createdAt siteUrl user { name } }
            }
          }
        }
        """;

    private readonly ReviewCache<string> _cache = new(TimeSpan.FromHours(12));

    public async Task<IReadOnlyList<MangaReview>?> GetReviewsAsync(int? aniListId, int? malId, CancellationToken ct = default)
    {
        if (aniListId is null && malId is null)
        {
            return [];
        }

        var key = aniListId is int al ? $"al:{al}" : $"mal:{malId}";
        if (_cache.TryGet(key, out var cached))
        {
            return cached;
        }

        var reviews = await FetchAsync(aniListId, malId, ct);
        if (reviews is not null)
        {
            _cache.Set(key, reviews);
        }

        return reviews;
    }

    private async Task<IReadOnlyList<MangaReview>?> FetchAsync(int? aniListId, int? malId, CancellationToken ct)
    {
        var variables = aniListId is int al
            ? new Dictionary<string, int> { ["id"] = al }
            : new Dictionary<string, int> { ["idMal"] = malId!.Value };

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsJsonAsync(string.Empty, new { query = Query, variables }, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            // AniList answers an unknown id with a 404 and `"Media": null`. That is "no reviews",
            // not an outage.
            if (response.StatusCode == HttpStatusCode.NotFound && body.Contains("\"Media\":null", StringComparison.Ordinal))
            {
                return [];
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("AniList reviews for {Key} returned {Status}", aniListId ?? malId, (int)response.StatusCode);
                return null;
            }

            return ParseReviews(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException ||
                                   (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Fetching AniList reviews for {Key} failed", aniListId ?? malId);
            return null;
        }
    }

    internal static List<MangaReview> ParseReviews(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var reviews = new List<MangaReview>();
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("Media", out var media) || media.ValueKind != JsonValueKind.Object ||
            !media.TryGetProperty("reviews", out var page) || page.ValueKind != JsonValueKind.Object ||
            !page.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Array)
        {
            return reviews;
        }

        foreach (var node in nodes.EnumerateArray())
        {
            if (reviews.Count >= MaxReviews)
            {
                break;
            }

            var title = String(node, "summary")?.Trim();
            var text = CleanBody(String(node, "body") ?? string.Empty);

            // Authors often open the body with the headline as a heading; it is shown as the title.
            if (title is { Length: > 0 } && text.StartsWith(title, StringComparison.OrdinalIgnoreCase))
            {
                text = text[title.Length..].TrimStart();
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            double? score = Int(node, "score") is int s and > 0 ? s / 10.0 : null;
            var date = Int(node, "createdAt") is int unix
                ? DateTimeOffset.FromUnixTimeSeconds(unix).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : null;
            var author = node.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
                ? String(user, "name")
                : null;

            reviews.Add(new MangaReview(
                author,
                score,
                ReviewText.Truncate(text),
                String(node, "siteUrl"),
                date,
                [],
                ReviewSource.AniList,
                title,
                Int(node, "rating")));
        }

        return reviews;
    }

    /// <summary>
    /// AniList's review markdown reduced to plain prose: spoilers and embedded media dropped,
    /// formatting marks and HTML tags stripped, entities decoded.
    /// </summary>
    internal static string CleanBody(string body)
    {
        var text = SpoilerRegex().Replace(body, string.Empty);
        text = EmbedRegex().Replace(text, string.Empty);
        text = LineBreakTagRegex().Replace(text, "\n");
        text = HtmlTagRegex().Replace(text, string.Empty);
        text = LinkRegex().Replace(text, "$1");
        text = text.Replace("~~~", string.Empty, StringComparison.Ordinal);
        text = RuleLineRegex().Replace(text, string.Empty);
        text = HeadingRegex().Replace(text, string.Empty);
        text = QuoteRegex().Replace(text, string.Empty);
        text = StrongRegex().Replace(text, string.Empty);
        text = ItalicRegex().Replace(text, "$1");
        text = WebUtility.HtmlDecode(text);
        text = TrailingSpaceRegex().Replace(text, "\n");
        return BlankLinesRegex().Replace(text, "\n\n").Trim();
    }

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    [GeneratedRegex(@"~!.*?!~", RegexOptions.Singleline)]
    private static partial Regex SpoilerRegex();

    // img(url), img220(url), img50%(url), webm(url), youtube(id)
    [GeneratedRegex(@"\b(?:img|webm|youtube)\d*%?\s*\([^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex EmbedRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTagRegex();

    [GeneratedRegex(@"<[^>\n]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\[([^\]\n]*)\]\([^)\s]*\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"^[ \t]*(?:[-_*][ \t]*){3,}$", RegexOptions.Multiline)]
    private static partial Regex RuleLineRegex();

    [GeneratedRegex(@"^[ \t]*#{1,6}[ \t]*", RegexOptions.Multiline)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^[ \t]*>[ \t]?", RegexOptions.Multiline)]
    private static partial Regex QuoteRegex();

    [GeneratedRegex(@"\*{2,}|_{2,}|~~")]
    private static partial Regex StrongRegex();

    [GeneratedRegex(@"(?<![\w*])[*_](?=\S)([^*_\n]+?)(?<=\S)[*_](?![\w*])")]
    private static partial Regex ItalicRegex();

    [GeneratedRegex(@"[ \t]+\r?\n|\r\n")]
    private static partial Regex TrailingSpaceRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLinesRegex();
}
