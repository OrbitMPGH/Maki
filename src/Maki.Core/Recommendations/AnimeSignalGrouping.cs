using Maki.Core.Entities;

namespace Maki.Core.Recommendations;

/// <summary>One stored anime-list row, flattened to what grouping reads.</summary>
public record AnimeSignalRow(
    string Service,
    long AnimeId,
    long? MalAnimeId,
    string? Title,
    int? Score,
    AnimeWatchStatus Status,
    long? MangaBakaId);

/// <summary>
/// One opinion about one work, after the rows that say it twice have been folded together.
/// </summary>
/// <param name="Key">
/// Stable across syncs, for a client that keys a list row on it. The manga id when there is one,
/// otherwise the anime's own dedupe key, so an unmatched entry is still its own row.
/// </param>
/// <param name="Services">Which trackers contributed, ordered, for the badges on the panel.</param>
/// <param name="AnimeCount">Distinct anime behind this group after cross-tracker dedupe: the season count.</param>
/// <param name="Score">The averaged score over the entries that carry one, or null when none does.</param>
public record AnimeSignalGroup(
    string Key,
    long? MangaBakaId,
    string Title,
    IReadOnlyList<string> Services,
    int AnimeCount,
    double? Score,
    AnimeWatchStatus Status)
{
    public AnimeSignalRole Role => AnimeSignalPolicy.RoleOf(Status, Score);
}

/// <summary>
/// Folds a reader's raw anime-list rows into one signal per work, which is the unit everything
/// downstream wants.
/// <para>
/// Two things make the raw rows unusable as they stand. A reader who scrobbles from Jellyfin to
/// both AniList and MyAnimeList has every show listed twice, and a franchise with four seasons is
/// four anime pointing at one manga, often with four different scores. Feeding those straight in
/// counts one opinion up to eight times and lets whichever season they liked most or least speak
/// for the whole thing.
/// </para>
/// <para>
/// So: dedupe across trackers first, average across seasons second. The order matters. Averaging
/// first would weight a show by how many trackers happen to list it, which is a fact about the
/// reader's scrobbler rather than about their taste.
/// </para>
/// </summary>
public static class AnimeSignalGrouping
{
    /// <summary>
    /// Which status speaks for a franchise. Finishing anything outranks abandoning anything else:
    /// somebody who completed three seasons and dropped the fourth liked the work, and reading that
    /// as a complaint would push away the manga they sat through sixty episodes of. A drop only
    /// carries the group when nothing in it was ever finished or is still being watched.
    /// </summary>
    private static int StatusRank(AnimeWatchStatus status) => status switch
    {
        AnimeWatchStatus.Completed => 0,
        AnimeWatchStatus.Watching => 1,
        AnimeWatchStatus.OnHold => 2,
        AnimeWatchStatus.Dropped => 3,
        _ => 4,
    };

    public static IReadOnlyList<AnimeSignalGroup> Group(IEnumerable<AnimeSignalRow> rows) =>
        rows
            .GroupBy(DedupeKey, StringComparer.Ordinal)
            .Select(MergeTrackers)
            .GroupBy(a => a.MangaBakaId is { } id ? $"manga:{id}" : $"anime:{a.Key}", StringComparer.Ordinal)
            .Select(MergeSeasons)
            .ToList();

    /// <summary>
    /// What makes two rows the same anime. The MyAnimeList id when both sides know it, which is the
    /// only exact answer available; otherwise the row stands alone under its own tracker's id.
    /// Titles are deliberately not a fallback: AniList serves English titles and MyAnimeList romaji,
    /// so matching on them would merge whatever happened to collide and miss everything else.
    /// </summary>
    private static string DedupeKey(AnimeSignalRow row) =>
        row.MalAnimeId is { } mal and > 0 ? $"mal-anime:{mal}" : $"{row.Service}:{row.AnimeId}";

    private static Anime MergeTrackers(IGrouping<string, AnimeSignalRow> rows)
    {
        // Ordered by service so the title, the manga id and the tie-breaks do not depend on the
        // order SQLite handed the rows back: an unordered pick would make the panel's text change
        // between two requests over identical data.
        var ordered = rows.OrderBy(r => r.Service, StringComparer.Ordinal).ThenBy(r => r.AnimeId).ToList();
        var rank = ordered.Min(r => StatusRank(r.Status));
        return new Anime(
            rows.Key,
            ordered.Select(r => r.MangaBakaId).FirstOrDefault(id => id is not null),
            ordered.Select(r => r.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? string.Empty,
            ordered.Select(r => r.Service).Distinct(StringComparer.Ordinal).ToList(),
            // Two trackers holding two scores for one show is a scrobbler that rounded differently,
            // or a rating the reader changed on one side and not the other. Either way it is one
            // opinion, so it averages rather than counting twice.
            Average(ordered.Select(r => (double?)r.Score)),
            ordered.First(r => StatusRank(r.Status) == rank).Status);
    }

    private static AnimeSignalGroup MergeSeasons(IGrouping<string, Anime> anime)
    {
        var all = anime.OrderBy(a => a.Key, StringComparer.Ordinal).ToList();

        // Plan-to-watch entries leave before the averaging. The policy already ignores them one by
        // one, and letting them in here would be a back door to the same mistake: a score typed
        // into an unwatched season's box would drag the franchise's average with it.
        var evidence = all.Where(a => a.Status != AnimeWatchStatus.Planning).ToList();
        if (evidence.Count == 0)
        {
            evidence = all;
        }

        var rank = evidence.Min(a => StatusRank(a.Status));
        return new AnimeSignalGroup(
            anime.Key,
            all.Select(a => a.MangaBakaId).FirstOrDefault(id => id is not null),
            // The shortest title in the group. Seasons are named by suffixing the first one
            // ("Vinland Saga", "Vinland Saga Season 2"), so the shortest is the franchise as the
            // reader would name it, and it is the one line on the panel that has to read well.
            all.OrderBy(a => a.Title.Length).ThenBy(a => a.Title, StringComparer.Ordinal)
                .Select(a => a.Title).FirstOrDefault(t => t.Length > 0) ?? string.Empty,
            all.SelectMany(a => a.Services).Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal).ToList(),
            all.Count,
            Average(evidence.Select(a => a.Score)),
            evidence.First(a => StatusRank(a.Status) == rank).Status);
    }

    /// <summary>
    /// The mean of the values that exist, or null when none does. Unscored entries are left out
    /// rather than counted as zero: not scoring a season says nothing about it, and folding that
    /// silence in as a 0 would turn a franchise somebody rated 9 into a 4.5.
    /// </summary>
    private static double? Average(IEnumerable<double?> scores)
    {
        var scored = scores.Where(s => s is not null).Select(s => s!.Value).ToList();
        return scored.Count > 0 ? scored.Average() : null;
    }

    /// <summary>One anime, after the trackers that both listed it were folded together.</summary>
    private sealed record Anime(
        string Key,
        long? MangaBakaId,
        string Title,
        IReadOnlyList<string> Services,
        double? Score,
        AnimeWatchStatus Status);
}
