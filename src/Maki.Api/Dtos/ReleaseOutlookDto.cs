using Maki.Core.Releases;

namespace Maki.Api.Dtos;

/// <summary>
/// What the series page and the Upcoming tab say about a series' next chapter. The window is a
/// span with a measured confidence, never a promised date; only <see cref="AnnouncedAt"/> is a
/// date, and only because a source stated it.
/// </summary>
/// <param name="Interval">Median gap between release days, in days.</param>
/// <param name="Spread">Median distance of a gap from <see cref="Interval"/>, in days.</param>
/// <param name="Hits">How many of the last <see cref="Checked"/> releases landed inside the window drawn for them.</param>
/// <param name="AnnouncedBy">Display name of the source that announced <see cref="AnnouncedAt"/>.</param>
/// <param name="Refreshing">False when the monitored refresh no longer visits the series (finished and caught up, or no usable source).</param>
public record ReleaseOutlookDto(
    CadenceState State,
    int ReleaseDays,
    double? Interval,
    double? Spread,
    DateTime? LastRelease,
    DateTime? WindowStart,
    DateTime? WindowEnd,
    double Confidence,
    int Hits,
    int Checked,
    DateTime? AnnouncedAt,
    string? AnnouncedBy,
    bool Refreshing,
    RefreshTier RefreshTier,
    DateTime? LastCheckAt,
    DateTime? NextCheckAt);

public record UpcomingSeriesDto(int SeriesId, string Title, string? CoverUrl, ReleaseOutlookDto Outlook);
