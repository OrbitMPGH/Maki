using Maki.Api.Services;

namespace Maki.Api.Dtos;

/// <summary>Where a reader stands overall. Window-independent, so fetched once per reader.</summary>
public record StatsStandingDto(
    ReadingBehaviour Behaviour,
    BacklogDto Backlog,
    IReadOnlyList<MidwaySeriesDto> Midway,
    IReadOnlyList<CreatorReturnDto> Creators,
    RatingGapDto? Ratings,
    long SeriesFullyRead);

/// <summary>Downloaded chapters the reader owns and has not read (watched counts as read).</summary>
/// <param name="UnreadChapters">Across the whole visible, non-incognito library, started or not.</param>
/// <param name="SeriesWithUnread">Across the whole visible, non-incognito library, started or not.</param>
/// <param name="HoursAtPace"><paramref name="UnreadChapters"/> at the reader's median pace; null when there is no pace yet.</param>
/// <param name="Top">The eight <em>started</em> series with the most unread chapters.</param>
public record BacklogDto(
    int UnreadChapters,
    int SeriesWithUnread,
    double? HoursAtPace,
    IReadOnlyList<BacklogSeriesDto> Top);

public record BacklogSeriesDto(int SeriesId, string Title, string? CoverUrl, int Read, int Unread);

/// <param name="Held">Downloaded chapters.</param>
/// <param name="EtaSeconds">Time to read the rest at the reader's median pace; null without one.</param>
/// <param name="LastReadAt">UTC.</param>
public record MidwaySeriesDto(
    int SeriesId, string Title, string? CoverUrl, int Read, int Held, int? EtaSeconds, DateTime LastReadAt);

/// <param name="Story">Credited as writer on at least one of the series.</param>
/// <param name="Art">Credited as artist on at least one of the series.</param>
public record CreatorReturnDto(string Name, bool Story, bool Art, int SeriesRead, int ChaptersRead);

/// <param name="Yours">The reader's rating, 1 to 10.</param>
/// <param name="Community">MangaBaka's rating scaled to 0 to 10.</param>
public record RatedSeriesDto(int SeriesId, string Title, string? CoverUrl, int Yours, double Community);

/// <param name="MeanGap">Mean of yours minus community; positive means the reader rates higher.</param>
/// <param name="Rated">Series with both ratings.</param>
/// <param name="Series">The twelve with the widest gap either way.</param>
public record RatingGapDto(double MeanGap, int Rated, IReadOnlyList<RatedSeriesDto> Series);
