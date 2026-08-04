using Maki.Core.Entities;

namespace Maki.Api.Dtos;

public record SeriesDto(
    int Id,
    string Title,
    string SortTitle,
    string? OriginalTitle,
    /// <summary>Other primary titles from the provider, for the "show more" expander next to <see cref="OriginalTitle"/>.</summary>
    List<string> AltTitles,
    string Status,
    string? Overview,
    int? Year,
    List<string> Genres,
    /// <summary>
    /// Provider-owned tags (<see cref="Series.Tags"/>) — finer-grained than genres and replaced on
    /// every metadata refresh. Distinct from <see cref="TagIds"/>, which the user assigns.
    /// </summary>
    List<string> MetadataTags,
    /// <summary>
    /// Ids of the user-assigned tags on this series (labels/colours come from <c>/api/v1/tags</c>,
    /// so a rename doesn't have to invalidate the whole library list). Empty on endpoints that
    /// don't load the tag navigation.
    /// </summary>
    List<int> TagIds,
    /// <summary>
    /// Whether anything is monitored — derived from <see cref="MonitorNewItems"/>, not a stored
    /// flag. Kept on the DTO so the UI has one thing to render, but it can never drift from the
    /// setting the way the old stored column did.
    /// </summary>
    bool Monitored,
    string MonitorNewItems,
    int RootFolderId,
    string FolderName,
    string? CoverUrl,
    int? TotalChapters,
    int? TotalVolumes,
    string? AuthorStory,
    string? AuthorArt,
    /// <summary>The user's own rating on a 1–10 scale, or null if unrated.</summary>
    int? Rating,
    int? MangaBakaId,
    int? AniListId,
    int? MalId,
    int? KitsuId,
    List<MetadataLink> Links,
    string? NumberingClash,
    DateTime Added,
    /// <summary>Chapters the user cares about: monitored, plus any already downloaded.</summary>
    int ChapterCount,
    int ChapterFileCount,
    /// <summary>
    /// Every chapter known to exist, monitored or not. Only differs from
    /// <see cref="ChapterCount"/> when unmonitored chapters have no file — the UI falls back to
    /// this so a series with nothing monitored reads "0 / 207" rather than a meaningless "0 / 0".
    /// </summary>
    int KnownChapterCount,
    /// <summary>Chapters queued but not yet actively downloading (Queued / RateLimited).</summary>
    int QueuedCount,
    /// <summary>Chapters actively in the download pipeline (fetching → importing).</summary>
    int DownloadingCount,
    bool HasAnime,
    string? AnimeName,
    string? AnimeStart,
    string? AnimeEnd,
    /// <summary>
    /// Downloaded chapters at or below the Rewind read high-water mark (Kavita/scrobble). Null
    /// when nothing has reported reading progress for this series yet — distinct from 0 (tracked,
    /// but nothing read).
    /// </summary>
    int? ReadChapterCount = null,
    /// <summary>
    /// Auto source matching is still queued or running (<see cref="Series.SourceMatchPending"/>).
    /// Adding a series returns before matching finishes, so the Sources card renders a spinner off
    /// this instead of "No sources linked", which would otherwise be what a freshly added series
    /// says for the half-minute the search takes.
    /// </summary>
    bool SourceMatchPending = false)
{
    /// <summary>
    /// Non-fatal problems from <c>Add</c> — the series exists, but something best-effort around it
    /// didn't (folder creation, source matching). Null everywhere else; the series still gets
    /// created, so these can't be errors, but silently returning 201 hid them entirely.
    /// </summary>
    public IReadOnlyList<string>? Warnings { get; init; }

    /// <summary>
    /// Where the UI fetches a series' poster. That route is one of the two API-key middleware
    /// carve-outs, so a plain <c>&lt;img src&gt;</c> loads it without a header.
    /// <para>
    /// Takes the two columns rather than the entity so the Home dashboard's rails — which project
    /// series into their own narrow shapes instead of materializing entities — can share it.
    /// </para>
    /// </summary>
    /// <param name="version">
    /// Stamped onto the URL as a cache-buster. The route itself never changes, so without this a
    /// browser that already rendered the cover keeps showing those bytes after a metadata refresh
    /// overwrites the file in place — same URL, no signal to refetch. <see cref="Series.LastMetadataRefresh"/>
    /// changes on every refresh (and is set on add), so it doubles as a free version stamp.
    /// </param>
    public static string? CoverUrlFor(int seriesId, string? coverPath, DateTime? version = null) =>
        coverPath != null
            ? $"/api/v1/mediacover/{seriesId}/cover.jpg?v={(version ?? DateTime.UtcNow).Ticks}"
            : null;

    /// <param name="rating">
    /// The <em>viewing user's</em> score, from their <c>UserSeriesState</c> row. Passed in rather than
    /// read off the entity because it is no longer on it: a shared column meant one person's score was
    /// what every other person saw, and what got pushed to their tracker profiles.
    /// </param>
    public static SeriesDto FromEntity(
        Series s, int chapterCount = 0, int chapterFileCount = 0, int knownChapterCount = 0,
        int queuedCount = 0, int downloadingCount = 0, int? readChapterCount = null,
        List<int>? tagIds = null, int? rating = null) => new(
        s.Id,
        s.Title,
        s.SortTitle,
        s.OriginalTitle,
        s.AltTitles,
        s.Status.ToString(),
        s.Overview,
        s.Year,
        s.Genres,
        s.Tags,
        tagIds ?? [.. s.UserTags.Select(t => t.Id)],
        s.MonitorNewItems != NewChapterMonitorMode.None,
        s.MonitorNewItems.ToString(),
        s.RootFolderId,
        s.FolderName,
        CoverUrlFor(s.Id, s.CoverPath, s.LastMetadataRefresh),
        s.TotalChapters,
        s.TotalVolumes,
        s.AuthorStory,
        s.AuthorArt,
        rating,
        s.MangaBakaId,
        s.AniListId,
        s.MalId,
        s.KitsuId,
        SeriesWebLinks.Labeled(s),
        s.NumberingClash,
        s.Added,
        chapterCount,
        chapterFileCount,
        knownChapterCount,
        queuedCount,
        downloadingCount,
        s.HasAnime,
        s.AnimeName,
        s.AnimeStart,
        s.AnimeEnd,
        readChapterCount,
        s.SourceMatchPending);
}

public record AddSeriesRequest(
    string MetadataProviderId,
    int RootFolderId,
    bool Monitored = true,
    string MonitorNewItems = "All");
