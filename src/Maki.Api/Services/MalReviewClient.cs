using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Maki.Api.Services;

/// <summary>
/// Fetches a handful of MyAnimeList reviews for a series for the Discover detail card.
///
/// This scrapes the review preview on MAL's main manga page directly, preserving MAL's selection.
/// Jikan's reviews endpoint previously returned HTTP 504 while MAL's own site still worked,
/// leaving every card showing "temporarily unavailable".
///
/// Best-effort: a failed fetch returns null (distinct from "fetched successfully, zero
/// reviews") so the UI can tell an outage apart from a series that genuinely has no reviews.
/// Results are cached per MAL id so opening the same card repeatedly stays cheap; failures are
/// not cached, so the next open retries instead of being stuck for 12 hours.
/// </summary>
public partial class MalReviewClient(IHttpClientFactory httpClientFactory, ILogger<MalReviewClient> logger)
{
    public const string HttpClientName = "mal-web";

    private const int MaxReviews = 3;

    private readonly ReviewCache<int> _cache = new(TimeSpan.FromHours(12));

    public async Task<IReadOnlyList<MangaReview>?> GetReviewsAsync(int malId, CancellationToken ct = default)
    {
        if (_cache.TryGet(malId, out var cached))
        {
            return cached;
        }

        var reviews = await FetchAsync(malId, ct);
        if (reviews is not null)
        {
            _cache.Set(malId, reviews);
        }

        return reviews;
    }

    private async Task<IReadOnlyList<MangaReview>?> FetchAsync(int malId, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            // The main page supplies MAL's chosen preview reviews, typically one per sentiment.
            // MAL accepts a placeholder title slug, so the id alone is enough.
            var html = await client.GetStringAsync($"manga/{malId}/_", ct);
            return ParseReviews(html);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Fetching MAL reviews for {MalId} failed", malId);
            return null;
        }
    }

    /// <summary>Parses the main manga page's review preview in MAL's display order.</summary>
    internal static List<MangaReview> ParseReviews(string html)
    {
        var reviews = new List<MangaReview>();

        // Each review is a `<div class="review-element js-review-element" ...>` block; split on the
        // marker and parse each fragment (the first split segment is the page chrome before any review).
        var blocks = html.Split("review-element js-review-element", StringSplitOptions.None);
        for (var i = 1; i < blocks.Length && reviews.Count < MaxReviews; i++)
        {
            var block = blocks[i];

            if (SpoilerMarkerRegex().IsMatch(block))
            {
                continue;
            }

            var text = ReviewTextRegex().Match(block);
            if (!text.Success)
            {
                continue;
            }

            var body = CleanText(text.Groups[1].Value);
            if (string.IsNullOrWhiteSpace(body))
            {
                continue;
            }

            body = ReviewText.Truncate(body);

            var author = FirstGroup(AuthorRegex().Match(block));
            var date = FirstGroup(DateRegex().Match(block));
            if (DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                date = parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            var url = UrlRegex().Match(block) is { Success: true } u
                ? "https://myanimelist.net/" + WebUtility.HtmlDecode(u.Groups[1].Value)
                : null;

            int? score = RatingRegex().Match(block) is { Success: true } r &&
                         int.TryParse(r.Groups[1].Value, out var s)
                ? s
                : null;

            var tags = new List<string>();
            if (SentimentRegex().Match(block) is { Success: true } sent)
            {
                tags.Add(sent.Groups[1].Value switch
                {
                    "recommended" => "discover.review.recommended",
                    "mixed-feelings" => "discover.review.mixedFeelings",
                    "not-recommended" => "discover.review.notRecommended",
                    _ => sent.Groups[1].Value,
                });
            }

            if (block.Contains("tag preliminary", StringComparison.Ordinal))
            {
                tags.Add("discover.review.preliminary");
            }

            reviews.Add(new MangaReview(author, score, body, url, date, tags));
        }

        return reviews;
    }

    /// <summary>Turns a review's inner HTML into readable plain text.</summary>
    private static string CleanText(string inner)
    {
        // MAL cuts a long review in two and hides the tail behind a "..." toggle. Stitch the halves
        // back together, markup whitespace and all, or the sentence the cut landed in the middle of
        // comes back split across a blank line.
        inner = ReadMoreSeamRegex().Replace(inner, " ");

        // MAL renders the stored review through nl2br, which emits "<br />\n": it inserts the tag
        // and keeps the author's original newline. Converting the tag on its own left both, so
        // every soft line break turned into a paragraph gap and the whole review read
        // double-spaced. The trailing newline belongs to the tag, so swallow it with the tag.
        inner = LineBreakRegex().Replace(inner, "\n");
        inner = TagRegex().Replace(inner, string.Empty);
        inner = WebUtility.HtmlDecode(inner);

        // Some reviews were stored already-encoded and come back double-encoded ("&amp;quot;"), so
        // one decode leaves a literal "&quot;" sitting in the prose. Decode a second time only when
        // an entity actually survived the first pass, rather than unconditionally.
        if (SurvivingEntityRegex().IsMatch(inner))
        {
            inner = WebUtility.HtmlDecode(inner);
        }

        // The page is pretty-printed, so lines carry the surrounding markup's indentation.
        inner = LineIndentRegex().Replace(inner, "\n");
        return CollapseBlankLinesRegex().Replace(inner, "\n\n").Trim();
    }

    private static string? FirstGroup(Match m) => m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : null;

    [GeneratedRegex(@"review-manga-reviewer"">([^<]+)<", RegexOptions.IgnoreCase)]
    private static partial Regex AuthorRegex();

    [GeneratedRegex(@"class=""update_at""[^>]*>([^<]+)<", RegexOptions.IgnoreCase)]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"(reviews\.php\?id=\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"Rating:\s*<span class=""num"">(\d+)</span>", RegexOptions.IgnoreCase)]
    private static partial Regex RatingRegex();

    [GeneratedRegex(@"<div class=""tag (recommended|mixed-feelings|not-recommended)", RegexOptions.IgnoreCase)]
    private static partial Regex SentimentRegex();

    // Deliberately inspect markup rather than review prose: a non-spoiler review can still discuss spoilers.
    [GeneratedRegex(@"class=""[^""]*\bspoiler(?:[-_][^""]*)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex SpoilerMarkerRegex();

    [GeneratedRegex(@"<div class=""text"">(.*?)</div>\s*<div class=""rating", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ReviewTextRegex();

    [GeneratedRegex(@"<br\s*/?>[ \t]*\r?\n?", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakRegex();

    [GeneratedRegex(
        @"\s*<span class=""js-visible[^""]*""[^>]*>\.\.\.</span>(?:\s*<span class=""js-hidden""[^>]*>)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex ReadMoreSeamRegex();

    [GeneratedRegex(@"&(?:amp|quot|apos|lt|gt|nbsp|#0?39);", RegexOptions.IgnoreCase)]
    private static partial Regex SurvivingEntityRegex();

    [GeneratedRegex(@"[ \t]*\r?\n[ \t]*")]
    private static partial Regex LineIndentRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex CollapseBlankLinesRegex();
}
