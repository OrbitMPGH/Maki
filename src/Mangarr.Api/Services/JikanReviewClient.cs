using System.Text.Json;

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
/// Fetches a handful of MyAnimeList reviews for a series via Jikan (the unofficial MAL API,
/// no auth needed). Best-effort: a failed fetch returns null (distinct from "fetched
/// successfully, zero reviews") so the UI can tell an outage apart from a series that
/// genuinely has no reviews. Results are cached per MAL id so opening the same card
/// repeatedly stays cheap and respects Jikan's rate limits; failures are not cached, so the
/// next open retries instead of being stuck showing "unavailable" for 12 hours.
/// </summary>
public class JikanReviewClient(IHttpClientFactory httpClientFactory, ILogger<JikanReviewClient> logger)
{
    public const string HttpClientName = "jikan";

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
            // Fetch failed (Jikan/MAL outage) — don't cache the failure, so the user can retry
            // by reopening the card instead of being stuck showing "unavailable" for 12 hours.
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
            // Include preliminary reviews — ongoing/hiatus series have only those, so filtering
            // them out would leave most popular titles with none. Spoilers are dropped below.
            await using var stream = await client.GetStreamAsync(
                $"v4/manga/{malId}/reviews", ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var reviews = new List<MangaReview>();
            foreach (var element in data.EnumerateArray())
            {
                if (element.TryGetProperty("is_spoiler", out var spoiler) &&
                    spoiler.ValueKind is JsonValueKind.True)
                {
                    continue;
                }

                var text = Str(element, "review");
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (text.Length > MaxTextLength)
                {
                    text = text[..MaxTextLength].TrimEnd() + "…";
                }

                var author = element.TryGetProperty("user", out var user)
                    ? Str(user, "username") ?? "Anonymous"
                    : "Anonymous";
                int? score = element.TryGetProperty("score", out var s) &&
                             s.ValueKind is JsonValueKind.Number
                    ? s.GetInt32()
                    : null;
                var tags = new List<string>();
                if (element.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var t in tagsEl.EnumerateArray())
                    {
                        if (t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } tag)
                        {
                            tags.Add(tag);
                        }
                    }
                }

                reviews.Add(new MangaReview(author, score, text, Str(element, "url"), Str(element, "date"), tags));
                if (reviews.Count >= MaxReviews)
                {
                    break;
                }
            }

            return reviews;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Jikan's /reviews endpoint scrapes MAL directly (not covered by MAL's own API) and
            // is prone to upstream outages independent of the rest of Jikan working fine — log
            // at Warning so that's visible instead of silently reading as "zero reviews".
            logger.LogWarning(ex, "Fetching MAL reviews for {MalId} failed", malId);
            return null;
        }
    }

    private static string? Str(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
