namespace Maki.Metadata.ReaderCohorts;

/// <summary>
/// Dials for the "readers like you" surfaces. Every value here came off
/// <c>distribution/eval-reader-cohorts.cs</c> on held-out readers, and the numbers behind them are
/// written up in <c>distribution/CLAUDE.md</c> under "v5: reader cohorts".
///
/// <para>
/// <b>The eval keeps its own copies of these, swept from the command line.</b> They must agree with
/// what ships or a sweep is measuring a configuration nobody runs — the same relationship
/// <c>UserFold.Of</c> has between the builder and the grader.
/// </para>
/// </summary>
public record ReaderCohortTuning
{
    public static ReaderCohortTuning Default { get; } = new();

    /// <summary>
    /// How many cohorts a reader is placed into. Five, and this is the value that makes the score
    /// accurate rather than merely different: at one cohort the strongest group holds all the
    /// weight, the number diverges from the catalogue average roughly twice as often, and it stops
    /// being measurably closer to the reader's own score at all (-0.047 MAE, 95% [-0.104, +0.009]).
    /// Averaging over five is simultaneously what earns the accuracy and what pulls the number back
    /// toward the middle.
    /// </summary>
    public int TopCohorts { get; init; } = 5;

    /// <summary>
    /// Readers, summed across the matched cohorts, who must have scored a series before their mean
    /// is worth anything. A mean over a handful of people is those people, not an average.
    /// </summary>
    public int MinRaters { get; init; } = 20;

    /// <summary>
    /// How far the cohort mean has to sit from the all-readers mean, in POINT_100 points, before
    /// there is anything to say. Five is half a star on the <c>/10</c> scale the detail card
    /// prints, i.e. the smallest gap a reader could actually see.
    /// <para>
    /// This gate is why the surface is a hint and not a second score. Measured on held-out readers:
    /// the cohort mean beats the plain item mean overall by 0.18 points, which is 0.018 of a
    /// rendered star and invisible, while its median distance from that mean is 1.87 points. Shown
    /// unconditionally it would be the same glyphs as the number beside it nine times in ten. On
    /// the 11.2% of series that clear this gate it is a different thing entirely: -0.656 MAE,
    /// 95% [-0.858, -0.446], 3.6x the pooled effect, with the direction of the gap matching which
    /// side of the average the reader's own score fell on 64.1% of the time.
    /// </para>
    /// </summary>
    public double MinDivergence { get; init; } = 5.0;

    /// <summary>
    /// How much of a series' overall popularity is divided back out of a cohort's completion rate
    /// when the rail ranks candidates: <c>rate / globalRate^gamma</c>. Zero is the raw rate, one is
    /// pure lift.
    ///
    /// <para>
    /// <b>Pure lift is not the answer, and assuming it was is the mistake this constant exists to
    /// record.</b> Dividing popularity out entirely returns titles so obscure that almost nobody
    /// goes on to finish them: measured over held-out readers, recall@40 collapses to 0.0051 and
    /// the median pick sits at popularity rank 46,285 of 128,116. The raw rate is the opposite
    /// failure, recall 0.1816 at rank 183 — the famous-with-everyone list the rail exists not to
    /// be. Across the sweep:
    /// </para>
    ///
    /// <code>
    /// gamma   recall@40   pop      cross-reader overlap
    /// 0.00    0.1816      183      0.230
    /// 0.25    0.1817      370      0.151
    /// 0.50    0.1513      1,449    0.098
    /// 0.75    0.0637      5,483    0.100
    /// 1.00    0.0051      46,285   0.140
    /// </code>
    ///
    /// <para>
    /// Ships at 0.5 rather than at the free 0.25 for a product reason rather than a metric one: it
    /// puts the rail's median pick at 1,449, which is where the recommender's own whole-library
    /// baseline already sits (1,489), so the rail matches the page it appears on instead of being
    /// visibly more mainstream than everything around it. The price is 17% of recall, measured.
    /// </para>
    /// </summary>
    public double PopularityDamping { get; init; } = 0.5;

    /// <summary>
    /// Candidates scored before the rail is cut to its display size. Generous because the filters
    /// and the owned-series exclusion both run after ranking, and a narrow shortlist would empty
    /// out on a large library.
    /// </summary>
    public int MaxCandidates { get; init; } = 600;
}
