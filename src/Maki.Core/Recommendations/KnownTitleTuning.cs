namespace Maki.Core.Recommendations;

public enum KnownTitleMode
{
    Off,

    /// <summary>Ranked lower by <see cref="KnownTitleTuning.WatchedDemotion"/>, still reachable.</summary>
    Demote,

    /// <summary>Left out of the similar picks. Relations and the anime resume rail still show it.</summary>
    Exclude,
}

/// <summary>
/// What Recommended does with titles the reader most likely already knows. Watched anime ships as
/// <see cref="KnownTitleMode.Exclude"/>; the demotions ship off.
/// <para>
/// Two cases, and they are different evidence. A manga whose anime is on the reader's own watch
/// list is known for certain, whatever score it has: an unscored or neutral row never seeds and
/// never avoids (<see cref="AnimeSignalGrouping.Group"/>), so without this it is an ordinary
/// candidate and comes straight back as a recommendation. A famous manga whose anime the reader
/// did <em>not</em> watch is only probably known, and only for a reader whose list shows they
/// watch a large share of such shows, so it is demoted in proportion and never removed.
/// </para>
/// </summary>
public sealed record KnownTitleTuning
{
    public static readonly KnownTitleTuning Default = new();

    /// <summary>
    /// Manga whose anime the reader knows (<see cref="AnimeSignalGrouping.WatchedManga"/>).
    /// </summary>
    public KnownTitleMode WatchedAnime { get; init; } = KnownTitleMode.Exclude;

    /// <summary>Score subtracted from a watched title under <see cref="KnownTitleMode.Demote"/>.</summary>
    public double WatchedDemotion { get; init; } = 1.0;

    /// <summary>
    /// The most score subtracted from a famous manga with an aired anime the reader did not watch.
    /// 0 is off.
    /// </summary>
    public double UnwatchedWeight { get; init; }

    /// <summary>Popularity rank past which nothing is assumed about a title the reader skipped.</summary>
    public int UnwatchedMaxRank { get; init; } = 1000;

    /// <summary>
    /// The share of the famous aired adaptations a reader must have on their list for a skipped one
    /// to count at full strength. Below it the demotion scales down linearly.
    /// </summary>
    public double FullAdaptationRate { get; init; } = 0.25;

    /// <summary>
    /// Fewer watched famous adaptations than this and the list says nothing about what was skipped.
    /// </summary>
    public int MinWatched { get; init; } = 20;

    public bool Active => WatchedAnime != KnownTitleMode.Off || UnwatchedWeight > 0;

    /// <summary>
    /// How strongly a skipped famous adaptation is demoted, in [0, 1]: the reader's adaptation rate
    /// against <see cref="FullAdaptationRate"/>, times how famous the title is on a log scale
    /// (1 at rank 1, 0 at <see cref="UnwatchedMaxRank"/>).
    /// </summary>
    /// <param name="watchedFamous">Famous aired adaptations on the reader's list.</param>
    /// <param name="famous">Famous aired adaptations in the catalogue.</param>
    public double UnwatchedStrength(long rank, int watchedFamous, int famous)
    {
        if (rank < 1 || rank > UnwatchedMaxRank || UnwatchedMaxRank < 2 || famous <= 0
            || watchedFamous < MinWatched || FullAdaptationRate <= 0)
        {
            return 0;
        }

        var rate = Math.Min(1, (double)watchedFamous / famous / FullAdaptationRate);
        var fame = 1 - (Math.Log(rank) / Math.Log(UnwatchedMaxRank));
        return rate * fame;
    }
}
