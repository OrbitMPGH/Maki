namespace Mangarr.Api.Dtos;

public record RewindTotalsDto(
    int ChaptersRead,
    int VolumesRead,
    int ChaptersDownloaded,
    int SeriesAdded,
    int SeriesRemoved,
    int SeriesFinished,
    int SeriesDropped);

/// <summary>One point of the activity timeline. Bucket is "yyyy-MM" or "yyyy-MM-dd" (local time).</summary>
public record RewindTimelinePointDto(
    string Bucket,
    int ChaptersRead,
    int ChaptersDownloaded,
    int SeriesAdded);

public record RewindSeriesStatDto(int? SeriesId, string Title, int Count);

public record RewindWeightedNameDto(string Name, int Weight);

public record RewindSeriesEventDto(int? SeriesId, string Title, DateTime At);

public record RewindDroppedSeriesDto(int? SeriesId, string Title, DateTime LastProgressAt, double MaxChapter);

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
    IReadOnlyList<RewindDroppedSeriesDto> Dropped);
