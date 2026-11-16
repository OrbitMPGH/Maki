namespace Maki.Api.Services;

/// <summary>
/// A single reader review, surfaced on the Discover detail card and the series page.
/// <para>
/// A null <see cref="Author"/> means "anonymous", and each of <see cref="Tags"/> is a catalogue
/// key rather than display text: the review clients are singletons caching per series id across
/// every caller, so they cannot render "Anonymous" or a sentiment label without freezing one
/// language into the cache for everyone who hits it before it expires. The controller renders them
/// with the caller's own <c>ILocalizer</c>.
/// </para>
/// <para>
/// <see cref="Score"/> is on a 10-point scale whatever the site uses. <see cref="Source"/> is one of
/// the <see cref="ReviewSource"/> keys, which match the <c>MetadataLink</c> site keys so the UI can
/// reuse its provider icons. <see cref="Title"/> is the review's own headline (AniList has one),
/// <see cref="Likes"/> its upvote count where the site exposes it, and <see cref="Date"/> is
/// <c>yyyy-MM-dd</c> when the site's date could be read, the site's raw text otherwise.
/// </para>
/// </summary>
public record MangaReview(
    string? Author,
    double? Score,
    string Text,
    string? Url,
    string? Date,
    IReadOnlyList<string> Tags,
    string Source = ReviewSource.MyAnimeList,
    string? Title = null,
    int? Likes = null);

public static class ReviewSource
{
    public const string MyAnimeList = "myanimelist";
    public const string AniList = "anilist";
    public const string Kitsu = "kitsu";
}

/// <summary>
/// Successful review fetches per series id, held for <paramref name="ttl"/>. Failures are never
/// stored, so reopening a card after an outage retries instead of staying stuck until expiry.
/// </summary>
internal sealed class ReviewCache<TKey>(TimeSpan ttl) where TKey : notnull
{
    private readonly Lock _lock = new();
    private readonly Dictionary<TKey, (DateTime At, IReadOnlyList<MangaReview> Reviews)> _entries = new();

    public bool TryGet(TKey key, out IReadOnlyList<MangaReview> reviews)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < ttl)
            {
                reviews = hit.Reviews;
                return true;
            }
        }

        reviews = [];
        return false;
    }

    public void Set(TKey key, IReadOnlyList<MangaReview> reviews)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            foreach (var expired in _entries.Where(e => now - e.Value.At >= ttl).Select(e => e.Key).ToList())
            {
                _entries.Remove(expired);
            }

            _entries[key] = (now, reviews);
        }
    }
}

internal static class ReviewText
{
    public const int MaxLength = 1400;

    public static string Truncate(string text, int max = MaxLength) =>
        text.Length > max ? text[..max].TrimEnd() + "…" : text;
}
