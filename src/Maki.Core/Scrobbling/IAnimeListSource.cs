using Maki.Core.Entities;

namespace Maki.Core.Scrobbling;

/// <summary>One row of a watcher's anime list, with whatever manga the provider volunteered.</summary>
/// <param name="MalAnimeId">
/// This <em>anime</em>'s MyAnimeList id, which is the one id both providers can name: AniList
/// carries it as <c>media.idMal</c> and MyAnimeList's own ids are it. That makes it the key that
/// tells "the same show, listed on two trackers" apart from "two shows", which nothing else here
/// can do - a reader who scrobbles to both has every entry twice. Null when AniList has no
/// cross-reference for the show.
/// </param>
/// <param name="RelationsResolved">
/// True when the list call itself carried the relation data, so the sync never has to ask again.
/// AniList sets it; MyAnimeList cannot, because its list endpoint has no relation field.
/// </param>
public record AnimeListEntry(
    long AnimeId,
    string Title,
    int? Score,
    AnimeWatchStatus Status,
    long? AniListMangaId = null,
    long? MalMangaId = null,
    bool RelationsResolved = false,
    long? MalAnimeId = null);

/// <summary>The manga one anime was adapted from, in whichever ids the provider knows.</summary>
public record AnimeRelatedManga(long? AniListMangaId, long? MalMangaId);

/// <summary>
/// A tracker that can also hand over the user's <em>anime</em> list. Separate from
/// <see cref="IScrobbleTracker"/> because scrobbling is a two-way manga sync and this is a one-way
/// read of a different medium: Kitsu and MangaBaka implement the first and not this.
/// </summary>
public interface IAnimeListSource
{
    Task<IReadOnlyList<AnimeListEntry>> ListAnimeAsync(int userId, CancellationToken ct = default);

    /// <summary>
    /// The manga behind one anime, for providers whose list call cannot say. Null means the anime
    /// has no manga relation, or that this provider does not answer the question at all.
    /// </summary>
    Task<AnimeRelatedManga?> RelatedMangaAsync(int userId, long animeId, CancellationToken ct = default);
}

/// <summary>One edge of an AniList <c>Media.relations</c> set, flattened for picking.</summary>
public readonly record struct AnimeMangaRelation(
    string RelationType, long Id, long? IdMal, string MediaType, string? Format);

/// <summary>
/// Which of an anime's related works is "the manga it came from".
/// <para>
/// AniList answers with every relation at once, including sequels, side stories and the light novel
/// a series was adapted from twice over. Ranking rather than filtering, because a franchise that has
/// no SOURCE edge usually still has an ADAPTATION one that is the right answer, and a set with both
/// a manga and a novel should land on the manga whichever order AniList returned them in.
/// </para>
/// </summary>
public static class AnimeRelationPicker
{
    private static int RelationRank(string relationType) => relationType.ToUpperInvariant() switch
    {
        "SOURCE" => 0,
        "ADAPTATION" => 1,
        "PARENT" => 2,
        "ALTERNATIVE" => 3,
        _ => int.MaxValue,
    };

    /// <summary>
    /// Prose formats only. AniList files light novels under <c>type: MANGA</c>, so without this a
    /// series adapted from a novel that also has a manga edge picks the novel - and the catalogue
    /// then drops it as a novel, losing a match that was sitting right there.
    /// </summary>
    private static int FormatRank(string? format) => (format ?? string.Empty).ToUpperInvariant() switch
    {
        "MANGA" => 0,
        "ONE_SHOT" => 1,
        "NOVEL" or "LIGHT_NOVEL" => int.MaxValue,
        // An unlabelled edge is still worth taking, below anything that named itself a manga.
        _ => 2,
    };

    /// <summary>
    /// Format before relation, which is the ordering that survives the light-novel case: a show with
    /// a SOURCE edge on its novel and an ADAPTATION edge on its manga wants the manga, because the
    /// novel is not a row the catalogue will ever return.
    /// </summary>
    public static AnimeMangaRelation? Pick(IEnumerable<AnimeMangaRelation> edges) =>
        edges
            .Where(e => string.Equals(e.MediaType, "MANGA", StringComparison.OrdinalIgnoreCase))
            .Where(e => RelationRank(e.RelationType) != int.MaxValue)
            .Where(e => FormatRank(e.Format) != int.MaxValue)
            .OrderBy(e => FormatRank(e.Format))
            .ThenBy(e => RelationRank(e.RelationType))
            .Select(e => (AnimeMangaRelation?)e)
            .FirstOrDefault();

    /// <summary>
    /// The same question for MyAnimeList, whose <c>related_manga</c> is already manga-only and
    /// carries no format. <see cref="int.MaxValue"/> means "not the work this anime came from", and
    /// the caller must skip those edges rather than fall back to them.
    /// <para>
    /// Strict for the same reason the AniList picker is: <c>related_manga</c> lists spin-offs,
    /// character books, side stories and art collections beside the source, and a fallback that took
    /// "whatever is left" would put a 4-koma gag spin-off into somebody's profile as though they had
    /// loved it. No relation at all is the right answer far more often than the wrong relation is.
    /// </para>
    /// </summary>
    public static int MalRelationRank(string? relationType) =>
        (relationType ?? string.Empty).ToLowerInvariant() switch
        {
            "adaptation" => 0,
            "parent_story" => 1,
            "full_story" => 2,
            _ => int.MaxValue,
        };
}
