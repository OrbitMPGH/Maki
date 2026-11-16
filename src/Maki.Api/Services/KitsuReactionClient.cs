using System.Globalization;
using System.Text.Json;

namespace Maki.Api.Services;

/// <summary>
/// The most upvoted Kitsu "reactions" for a series: one 140-character take per reader, attached to
/// their library entry, which is where the score comes from. Kitsu's long-form reviews stopped in
/// 2019, so these are what is still being written there. Shown below the full reviews as quick
/// takes. Same null-versus-empty and cache-only-successes contract as <see cref="MalReviewClient"/>.
/// </summary>
public class KitsuReactionClient(IHttpClientFactory httpClientFactory, ILogger<KitsuReactionClient> logger)
{
    public const string HttpClientName = "kitsu-reactions";

    private const int MaxReactions = 2;

    private readonly ReviewCache<int> _cache = new(TimeSpan.FromHours(12));

    public async Task<IReadOnlyList<MangaReview>?> GetReactionsAsync(int kitsuId, CancellationToken ct = default)
    {
        if (_cache.TryGet(kitsuId, out var cached))
        {
            return cached;
        }

        var reactions = await FetchAsync(kitsuId, ct);
        if (reactions is not null)
        {
            _cache.Set(kitsuId, reactions);
        }

        return reactions;
    }

    private async Task<IReadOnlyList<MangaReview>?> FetchAsync(int kitsuId, CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            // A few spares for held (moderated) reactions, which the API still lists.
            var json = await client.GetStringAsync(
                $"media-reactions?filter[mangaId]={kitsuId}&sort=-upVotesCount&page[limit]=5" +
                "&include=user,libraryEntry&fields[users]=name&fields[libraryEntries]=ratingTwenty",
                ct);
            return ParseReactions(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException ||
                                   (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(ex, "Fetching Kitsu reactions for {KitsuId} failed", kitsuId);
            return null;
        }
    }

    internal static List<MangaReview> ParseReactions(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var reactions = new List<MangaReview>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return reactions;
        }

        var included = new Dictionary<(string, string), JsonElement>();
        if (root.TryGetProperty("included", out var inc) && inc.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in inc.EnumerateArray())
            {
                if (String(item, "type") is { } type && String(item, "id") is { } id &&
                    item.TryGetProperty("attributes", out var attrs))
                {
                    included[(type, id)] = attrs;
                }
            }
        }

        foreach (var item in data.EnumerateArray())
        {
            if (reactions.Count >= MaxReactions)
            {
                break;
            }

            if (!item.TryGetProperty("attributes", out var attrs) ||
                String(attrs, "heldReason") is not null ||
                String(attrs, "reaction")?.Trim() is not { Length: > 0 } text)
            {
                continue;
            }

            var author = Related(item, "user", included) is { } user ? String(user, "name") : null;
            double? score = Related(item, "libraryEntry", included) is { } entry && Int(entry, "ratingTwenty") is int r and > 0
                ? r / 2.0
                : null;
            var date = String(attrs, "createdAt") is { } created &&
                       DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                ? at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : null;

            reactions.Add(new MangaReview(
                author,
                score,
                text,
                Url: null,
                date,
                [],
                ReviewSource.Kitsu,
                Likes: Int(attrs, "upVotesCount")));
        }

        return reactions;
    }

    private static JsonElement? Related(
        JsonElement item, string relationship, Dictionary<(string, string), JsonElement> included)
    {
        if (item.TryGetProperty("relationships", out var rels) &&
            rels.TryGetProperty(relationship, out var rel) &&
            rel.TryGetProperty("data", out var link) && link.ValueKind == JsonValueKind.Object &&
            String(link, "type") is { } type && String(link, "id") is { } id &&
            included.TryGetValue((type, id), out var attrs))
        {
            return attrs;
        }

        return null;
    }

    private static string? String(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
