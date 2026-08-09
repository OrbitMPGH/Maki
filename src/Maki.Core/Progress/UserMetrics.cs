namespace Maki.Core.Progress;

/// <summary>
/// Everything the achievement catalog can be evaluated against, for one user, at one moment.
/// <para>
/// Recomputed from the <c>StatsEvents</c> log rather than maintained incrementally, following the
/// same discipline as the reading marks: a counter that is only ever added to has no way back once it
/// is wrong, and needs a repair path nobody remembers to run. Deriving it also means a release that
/// adds an achievement backfills it the first time the user loads a page, and that
/// <c>IncognitoMode.Full</c> reading is excluded for free, since it never became an event.
/// </para>
/// <para>
/// The two library fields are the instance's, not this user's: <c>SeriesAdded</c> and
/// <c>ChapterDownloaded</c> events carry a null <c>UserId</c> by design, because they describe what is
/// on disk and not who read it.
/// </para>
/// </summary>
public record UserMetrics
{
    public long ChaptersRead { get; init; }
    public long VolumesRead { get; init; }
    public long ReadingSeconds { get; init; }
    public long SeriesFinished { get; init; }

    /// <summary>Distinct local dates with any reading activity.</summary>
    public long DaysRead { get; init; }

    public long CurrentStreak { get; init; }
    public long LongestStreak { get; init; }

    /// <summary>Distinct genres across every series this user has read at least one chapter of.</summary>
    public long DistinctGenres { get; init; }

    /// <summary>Distinct <c>Series.Type</c> values read, lowercased. Null types are not counted.</summary>
    public IReadOnlySet<string> TypesRead { get; init; } = new HashSet<string>();

    /// <summary>Most reading seconds banked in a single local day.</summary>
    public long BestDaySeconds { get; init; }

    /// <summary>Most reading seconds across one Saturday and the Sunday that follows it.</summary>
    public long BestWeekendSeconds { get; init; }

    /// <summary>Chapter count of the longest series this user has finished.</summary>
    public long LongestSeriesFinished { get; init; }

    /// <summary>Series where every downloaded chapter is marked read.</summary>
    public long SeriesFullyRead { get; init; }

    public bool ReadAfterMidnight { get; init; }
    public bool ReadAtDawn { get; init; }
    public bool ResumedAbandonedSeries { get; init; }
    public bool ReadOnNewYearsDay { get; init; }

    // Instance-wide, from the null-UserId events.
    public long LibrarySeries { get; init; }
    public long ChaptersDownloaded { get; init; }

    /// <summary>Local dates with activity, newest first — the contribution grid's source.</summary>
    public IReadOnlyList<ReadingDay> Days { get; init; } = [];
}

/// <summary>One local day's reading, for the contribution grid.</summary>
public record ReadingDay(DateOnly Date, int Chapters, int Seconds);
