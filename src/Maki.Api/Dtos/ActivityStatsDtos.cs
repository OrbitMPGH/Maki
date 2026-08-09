namespace Maki.Api.Dtos;

public record ActivityTotalsDto(
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
public record ActivityTimelinePointDto(
    string Bucket,
    int ChaptersRead,
    int ChaptersDownloaded,
    int SeriesAdded,
    /// <summary>Zero for a Kavita-only history, same caveat as <see cref="ActivityTotalsDto.ReadingSeconds"/>.</summary>
    int ReadingSeconds);

public record ActivitySeriesStatDto(int? SeriesId, string Title, int Count, string? CoverUrl);

/// <summary>
/// Time spent on one series. Kept apart from <see cref="ActivitySeriesStatDto"/> rather than
/// bolted onto it: the two rank differently (a long webtoon binge beats a slim volume on
/// chapters and loses on minutes) and only one of them can be built from Kavita's numbers.
/// </summary>
public record ActivitySeriesTimeDto(int? SeriesId, string Title, int Seconds, string? CoverUrl);

public record ActivityWeightedNameDto(string Name, int Weight);

/// <summary>
/// CoverUrl is null for a series that has since been removed: the title is denormalized onto the
/// event and survives, the cover file does not.
/// </summary>
public record ActivitySeriesEventDto(int? SeriesId, string Title, DateTime At, string? CoverUrl);

public record ActivityDroppedSeriesDto(
    int? SeriesId, string Title, DateTime LastProgressAt, double MaxChapter, string? CoverUrl);

public record ActivityStatsDto(
    DateOnly From,
    DateOnly To,
    bool ReadTrackingAvailable,
    ActivityTotalsDto Totals,
    IReadOnlyList<ActivityTimelinePointDto> Timeline,
    IReadOnlyList<ActivitySeriesStatDto> TopRead,
    IReadOnlyList<ActivitySeriesStatDto> LeastRead,
    IReadOnlyList<ActivityWeightedNameDto> TopGenres,
    IReadOnlyList<ActivityWeightedNameDto> TopTags,
    IReadOnlyList<ActivitySeriesEventDto> Finished,
    IReadOnlyList<ActivitySeriesEventDto> Added,
    IReadOnlyList<ActivitySeriesEventDto> Removed,
    IReadOnlyList<ActivityDroppedSeriesDto> Dropped,
    IReadOnlyList<ActivitySeriesTimeDto> TopByTime);
