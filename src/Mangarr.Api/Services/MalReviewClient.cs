using System.Net;
using System.Text.RegularExpressions;

namespace Mangarr.Api.Services;

/// <summary>A single reader review, surfaced on the Discover detail card.</summary>
public record MangaReview(
    string Author,
    int? Score,
    string Text,
    string? Url,
    string? Date,
    IReadOnlyList<string> Tags);

/// <summary>
/// Fetches a handful of MyAnimeList reviews for a series for the Discover detail card.
///
/// This scrapes MAL's public reviews page directly. The obvious route — Jikan, the unofficial
/// MAL API — has a <c>/manga/{id}/reviews</c> endpoint that scrapes MAL itself and is
/// chronically broken (returns HTTP 504 "Jikan failed to connect to MyAnimeList" even while
/// the rest of Jikan and MAL's own site work fine), so it left every card showing
/// "temporarily unavailable". Going straight to the HTML MAL serves is the reliable path.
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
    private const int MaxTextLength = 1400;
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(12);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<int, (DateTime At, IReadOnlyList<MangaReview> Reviews)> _cache = new();

    public async Task<IReadOnlyList<MangaReview>?> GetReviewsAsync(int malId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(malId, out var hit) && DateTime.UtcNow - hit.At < CacheFor)
            {
                return hit.Reviews;
            }
        }
        finally
        {
            _lock.Release();
        }

        var reviews = await FetchAsync(malId, ct);
        if (reviews is null)
        {
            // Fetch failed (MAL outage / blocked) — don't cache the failure, so the user can
            // retry by reopening the card instead of being stuck showing "unavailable".
            return null;
        }

        await _lock.WaitAsync(ct);
        try
        {
            _cache[malId] = (DateTime.UtcNow, reviews);
        }
        finally
        {
            _lock.Release();
        }

        return reviews;
    }

    private async Task<IReadOnlyList<MangaReview>?> FetchAsync(int malId, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            // The title slug is required in the path but MAL accepts a placeholder, so the id alone
            // is enough. Sorted most-helpful-first by default, which is what we want for a preview.
            var html = await client.GetStringAsync($"manga/{malId}/_/reviews", ct);
            return ParseReviews(html);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Fetching MAL reviews for {MalId} failed", malId);
            return null;
        }
    }

    /// <summary>Parses MAL's reviews page HTML into the first few reviews.</summary>
    private static List<MangaReview> ParseReviews(string html)
    {
        var reviews = new List<MangaReview>();

        // Each review is a `<div class="review-element js-review-element" ...>` block; split on the
        // marker and parse each fragment (the first split segment is the page chrome before any review).
        var blocks = html.Split("review-element js-review-element", StringSplitOptions.None);
        for (var i = 1; i < blocks.Length && reviews.Count < MaxReviews; i++)
        {
            var block = blocks[i];

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

            if (body.Length > MaxTextLength)
            {
                body = body[..MaxTextLength].TrimEnd() + "…";
            }

            var author = FirstGroup(AuthorRegex().Match(block)) ?? "Anonymous";
            var date = FirstGroup(DateRegex().Match(block));
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
                    "recommended" => "Recommended",
                    "mixed-feelings" => "Mixed Feelings",
                    "not-recommended" => "Not Recommended",
                    _ => sent.Groups[1].Value,
                });
            }

            if (block.Contains("tag preliminary", StringComparison.Ordinal))
            {
                tags.Add("Preliminary");
            }

            reviews.Add(new MangaReview(author, score, body, url, date, tags));
        }

        return reviews;
    }

    /// <summary>Turns a review's inner HTML into readable plain text.</summary>
    private static string CleanText(string inner)
    {
        // <br> → newline; drop the "…" read-more toggle marker but keep the hidden continuation.
        inner = LineBreakRegex().Replace(inner, "\n");
        inner = ReadMoreMarkerRegex().Replace(inner, string.Empty);
        inner = TagRegex().Replace(inner, string.Empty);
        inner = WebUtility.HtmlDecode(inner);
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

    [GeneratedRegex(@"<div class=""text"">(.*?)</div>\s*<div class=""rating", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ReviewTextRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakRegex();

    [GeneratedRegex(@"<span class=""js-visible[^""]*""[^>]*>\.\.\.</span>", RegexOptions.IgnoreCase)]
    private static partial Regex ReadMoreMarkerRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex CollapseBlankLinesRegex();
}
