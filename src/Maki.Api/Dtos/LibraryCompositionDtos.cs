namespace Maki.Api.Dtos;

public record LibraryCompositionTotalsDto(
    int SeriesCount,
    int MonitoredCount,
    int CompletedCount,
    int ChapterCount,
    int DownloadedChapterCount,
    int FileCount,
    long TotalBytes);

public record NamedCountDto(string Name, int Count);

public record SourceUsageDto(string Name, int Files, long Bytes);

/// <summary>
/// One local day ("yyyy-MM-dd") or month ("yyyy-MM"): series and bytes added, with the library's
/// series count and disk use at the end of it. Bytes come from the files on disk now, by when each
/// was added, so a deleted file drops out of every bucket rather than showing as a dip.
/// </summary>
public record LibraryGrowthDto(string Bucket, int SeriesAdded, int Cumulative, long BytesAdded, long CumulativeBytes);

public record SeriesSizeDto(int SeriesId, string Title, string? CoverUrl, int Files, long Bytes);

/// <summary>Download queue health for one source over the trailing 30 days. See <see cref="LibraryCompositionDto.SourceReliability"/>.</summary>
public record SourceReliabilityDto(string Name, int Completed, int Failed, int? MedianSecondsToComplete);

/// <summary>Series request backlog. See <see cref="LibraryCompositionDto.Requests"/>.</summary>
public record RequestSummaryDto(bool AllUsers, int Open, int Resolved90d, double? MedianResolveHours);

/// <summary>
/// What the collection is made of, as opposed to what anyone read. Not per-user: the library is
/// shared. Per-user visibility still applies, through the root-folder query filter on
/// <c>Series</c> and the matching one on <c>ChapterFile</c>.
/// </summary>
public record LibraryCompositionDto(
    LibraryCompositionTotalsDto Totals,
    IReadOnlyList<NamedCountDto> ByType,
    IReadOnlyList<NamedCountDto> ByStatus,
    IReadOnlyList<SourceUsageDto> BySource,
    IReadOnlyList<NamedCountDto> TopGenres,
    IReadOnlyList<SeriesSizeDto> Largest,
    IReadOnlyList<NamedCountDto> ByContentRating,
    IReadOnlyList<SourceReliabilityDto> SourceReliability,
    RequestSummaryDto Requests,
    int MonitorCatches);
