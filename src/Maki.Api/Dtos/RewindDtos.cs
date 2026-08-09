namespace Maki.Api.Dtos;

public record RewindTotalsDto(
    int ChaptersRead,
    int VolumesRead,
    int ChaptersDownloaded,
    int SeriesAdded,
    int SeriesRemoved,
    int SeriesFinished,
    int SeriesDropped,
    /// <summary>
    /// Active seconds in the built-in reader. Kavita reports what was read, never for how long,
    /// so this counts reading done here and nothing else — it is legitimately zero for somebody
    /// whose whole history came in over the Kavita pass.
    /// </summary>
    int ReadingSeconds,
    /// <summary>
    /// Local dates in the window on which anything was read. Counted from read events rather than
    /// from <see cref="ReadingSeconds"/> so a Kavita-only user still gets a number.
    /// </summary>
    int DaysActive);

/// <summary>One point of the activity timeline. Bucket is "yyyy-MM" or "yyyy-MM-dd" (local time).</summary>
public record RewindTimelinePointDto(
    string Bucket,
    int ChaptersRead,
    int ChaptersDownloaded,
    int SeriesAdded,
    /// <summary>Zero for a Kavita-only history, same caveat as <see cref="RewindTotalsDto.ReadingSeconds"/>.</summary>
    int ReadingSeconds);

public record RewindSeriesStatDto(int? SeriesId, string Title, int Count, string? CoverUrl);

/// <summary>
/// Time spent on one series. Kept apart from <see cref="RewindSeriesStatDto"/> rather than
/// bolted onto it: the two rank differently (a long webtoon binge beats a slim volume on
/// chapters and loses on minutes) and only one of them can be built from Kavita's numbers.
/// </summary>
public record RewindSeriesTimeDto(int? SeriesId, string Title, int Seconds, string? CoverUrl);

public record RewindWeightedNameDto(string Name, int Weight);

/// <summary>
/// CoverUrl is null for a series that has since been removed: the title is denormalized onto the
/// event and survives, the cover file does not.
/// </summary>
public record RewindSeriesEventDto(int? SeriesId, string Title, DateTime At, string? CoverUrl);

public record RewindDroppedSeriesDto(
    int? SeriesId, string Title, DateTime LastProgressAt, double MaxChapter, string? CoverUrl);

public record RewindStatsDto(
    DateOnly From,
    DateOnly To,
    bool ReadTrackingAvailable,
    RewindTotalsDto Totals,
    IReadOnlyList<RewindTimelinePointDto> Timeline,
    IReadOnlyList<RewindSeriesStatDto> TopRead,
    IReadOnlyList<RewindSeriesStatDto> LeastRead,
    IReadOnlyList<RewindWeightedNameDto> TopGenres,
    IReadOnlyList<RewindWeightedNameDto> TopTags,
    IReadOnlyList<RewindSeriesEventDto> Finished,
    IReadOnlyList<RewindSeriesEventDto> Added,
    IReadOnlyList<RewindSeriesEventDto> Removed,
    IReadOnlyList<RewindDroppedSeriesDto> Dropped,
    IReadOnlyList<RewindSeriesTimeDto> TopByTime);
