using Maki.Core.Entities;

namespace Maki.Core.Recommendations;

/// <summary>
/// One anime on the reader's list after cross-tracker dedupe, with seasons kept apart. This is the
/// per-season view that <see cref="AnimeSignalGrouping.Watched"/> produces for the "start where the
/// anime ended" resolver. <see cref="AnimeSignalGroup"/> is the per-franchise view the recommender
/// uses; do not feed this record into seeding.
/// </summary>
/// <param name="AnimeId">The dedupe key's anime id (AniList id when known, otherwise the MAL id).</param>
/// <param name="Format">Tracker media format, upper-cased: TV, TV_SHORT, ONA, MOVIE, OVA, SPECIAL, MUSIC. Null until the first sync after the column was added.</param>
/// <param name="Episodes">Total episode count when the tracker knows it.</param>
/// <param name="Progress">Episodes the reader has watched.</param>
public record AnimeWatchedSeason(
    long AnimeId,
    long? MalAnimeId,
    string Title,
    IReadOnlyList<string> Services,
    int? Score,
    AnimeWatchStatus Status,
    string? Format,
    DateOnly? StartDate,
    DateOnly? EndDate,
    int? Episodes,
    int? Progress,
    long? MangaBakaId);
