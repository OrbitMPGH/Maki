namespace Maki.Api.Dtos;

/// <param name="TimeZone">
/// The zone the window and the rhythm matrix were bucketed in: the reader's stored zone id, or a
/// fixed offset such as "UTC+02:00" when they never picked one.
/// </param>
public record StatsInsightsDto(
    DateOnly From,
    DateOnly To,
    string TimeZone,
    RhythmDto Rhythm,
    SittingsDto? Sittings,
    TasteMixDto Taste,
    int BookmarksAdded);

/// <summary>
/// When reading happens, from the built-in reader's timed events only. Everything but the matrix and
/// the total is null when no time was recorded, which is every Kavita-only reader.
/// </summary>
/// <param name="SecondsByWeekdayHour">168 cells, weekday * 24 + hour, Monday = 0.</param>
/// <param name="PrimeStartHour">Start of the busiest three-hour window; it may wrap past midnight.</param>
/// <param name="BusiestWeekday">Monday = 0.</param>
public record RhythmDto(
    IReadOnlyList<int> SecondsByWeekdayHour,
    int TotalSeconds,
    int? PrimeStartHour,
    double? PrimeShare,
    int? BusiestWeekday,
    double? WeekendShare);

/// <param name="LongestStartedAt">UTC, like <see cref="MidwaySeriesDto.LastReadAt"/>.</param>
/// <param name="PerWeek">Sittings per seven days of the window.</param>
public record SittingsDto(
    int Count,
    int MedianSeconds,
    int LongestSeconds,
    DateTime LongestStartedAt,
    double PerWeek,
    int ChaptersPerSittingMedian);

/// <param name="ReadShare">Share of chapters read in the window from series carrying this genre.</param>
/// <param name="LibraryShare">Share of the visible library carrying this genre.</param>
public record GenreLeanDto(string Name, double ReadShare, double LibraryShare);

/// <param name="Decade">Release decade, e.g. 2010; null for series with no year.</param>
public record EraBucketDto(int? Decade, int Chapters);

/// <param name="Lean">Up to five genres read more than the library holds, then up to five read less.</param>
/// <param name="Types">Chapters read by series type; "" for series with no type.</param>
/// <param name="Demographics">Chapters read by demographic genre; "" for series with none.</param>
public record TasteMixDto(
    IReadOnlyList<GenreLeanDto> Lean,
    IReadOnlyList<NamedCountDto> Types,
    IReadOnlyList<NamedCountDto> Demographics,
    IReadOnlyList<EraBucketDto> Eras);
