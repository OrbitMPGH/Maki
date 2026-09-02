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
    /// there is anything to say.
    /// <para>
    /// This gate is why the surface is a hint and not a second score. Measured on held-out readers:
    /// the cohort mean beats the plain item mean overall by 0.13 points, which is 0.013 of a
    /// rendered star and invisible, while its median distance from that mean is 1.41 points. Shown
    /// unconditionally it would be the same glyphs as the number beside it nine times in ten.
    /// </para>
    /// <para>
    /// <b>Four rather than five, and the reason is that the mix stopped being sharpened.</b> Half a
    /// star on the <c>/10</c> scale the detail card prints is five points, which looks like the
    /// natural floor and was the shipped value while <c>Place</c> subtracted the weakest kept
    /// cohort. Removing that subtraction pulled the cohort mean back toward the crowd, so at five
    /// the hint fired on 4.8% of predictions instead of 11.3%. The gate is a frequency dial, not a
    /// legibility one, and moving it to four restores the old frequency at better accuracy than the
    /// old configuration ever had:
    /// </para>
    ///
    /// <code>
    /// weights            gate   fires   MAE against the item mean
    /// floor-subtracted   5.0    11.3%   -0.544, 95% [-0.726, -0.365]
    /// proportional       5.0     4.8%   -0.922, 95% [-1.184, -0.654]
    /// proportional       4.0     9.2%   -0.748, 95% [-0.901, -0.609]   <-- ships
    /// </code>
    ///
    /// <para>
    /// The direction of the gap matches which side of the average the reader's own score fell on
    /// 63.8% of the time, against 63.2% before. Four points is 0.4 of a rendered star, still a
    /// visible difference in the digits the modal prints.
    /// </para>
    /// </summary>
    public double MinDivergence { get; init; } = 4.0;

    /// <summary>
    /// How much of a series' overall popularity is divided back out of a cohort's completion rate
    /// when the rail ranks candidates: <c>rate / globalRate^gamma</c>. Zero is the raw rate, one is
    /// pure lift.
    ///
    /// <para>
    /// <b>Pure lift is not the answer, and assuming it was is the mistake this constant exists to
    /// record.</b> Dividing popularity out entirely returns titles so obscure that almost nobody
    /// goes on to finish them: measured over held-out readers, recall@40 collapses to 0.0016 and
    /// the median pick sits at popularity rank 44,132 of 128,116. The raw rate is the opposite
    /// failure, recall 0.1653 at rank 168 — the famous-with-everyone list the rail exists not to
    /// be. Across the sweep:
    /// </para>
    ///
    /// <code>
    /// gamma   recall@40   pop      cross-reader overlap
    /// 0.00    0.1653      168      0.280
    /// 0.25    0.1556      304      0.243
    /// 0.50    0.1110      1,142    0.247
    /// 0.75    0.0513      5,580    0.283
    /// 1.00    0.0016      44,132   0.310
    /// </code>
    ///
    /// <para>
    /// Ships at 0.5 rather than at the cheaper 0.25 for a product reason rather than a metric one:
    /// it puts the rail's median pick at 1,142, near where the recommender's own whole-library
    /// baseline sits (1,489), so the rail matches the page it appears on instead of being visibly
    /// more mainstream than everything around it. 0.25 would buy back all of the recall the slot
    /// draw costs and then some (0.1556 against the old ranking's 0.1528) at a median pick of 304,
    /// which is a fame chart again.
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
