namespace Maki.Core.Releases;

/// <summary>What the upcoming estimate can honestly say about a series. Sent to the client by name.</summary>
public enum CadenceState
{
    /// <summary>Fewer than <see cref="ReleaseCadence.MinReleaseDays"/> release days to go on.</summary>
    Unknown,

    /// <summary>A typical gap exists, but past windows missed too often (or the gap is too long) to draw one.</summary>
    Irregular,

    /// <summary>The window has not closed yet.</summary>
    Expected,

    /// <summary>The window closed without a chapter, but not by so much that it means nothing yet.</summary>
    Late,

    /// <summary>So far past the window that the old rhythm says nothing about the next chapter.</summary>
    Stalled
}

/// <param name="Interval">Median gap between release days, in days.</param>
/// <param name="Spread">Median distance of a gap from that median (MAD), in days.</param>
/// <param name="Confidence">
/// How often this series' own past releases landed inside the window this method would have drawn
/// for them, smoothed toward one half so a short history cannot read as certain.
/// </param>
/// <param name="Hits">Past releases that landed inside their window.</param>
/// <param name="Checked">Past releases tested.</param>
public sealed record ReleaseCadenceEstimate(
    CadenceState State,
    int ReleaseDays,
    double? Interval,
    double? Spread,
    DateTime? LastRelease,
    DateTime? WindowStart,
    DateTime? WindowEnd,
    double Confidence,
    int Hits,
    int Checked);

/// <summary>
/// Estimates when a series' next chapter is likely, as a window rather than a date, with a
/// confidence measured on the series' own history. Release times collapse to distinct UTC days
/// first, so a same-day batch is one release. See docs/design-release-aware-polling.md for the
/// measurements behind the thresholds.
/// </summary>
public static class ReleaseCadence
{
    public const int MinReleaseDays = 4;

    /// <summary>Gaps the interval is taken over: the last ten, like Mihon's window.</summary>
    public const int RecentGaps = 10;

    /// <summary>Past releases tested for the confidence.</summary>
    public const int Backtest = 10;

    /// <summary>Below this many tested releases there is no window, whatever the hit rate.</summary>
    public const int MinChecked = 3;

    /// <summary>Better than a coin flip: with the smoothing, more than half of the tested releases hit.</summary>
    public const double MinConfidence = 0.55;
    public const double HighConfidence = 0.75;

    /// <summary>A longer typical gap is too sparse to draw a useful window for.</summary>
    public const double MaxIntervalDays = 90;

    public static ReleaseCadenceEstimate Estimate(IEnumerable<DateTime> releases, DateTime now)
    {
        var times = releases.Where(r => r <= now).Order().ToList();
        var days = times.Select(t => DateOnly.FromDateTime(t)).Distinct().ToList();
        if (days.Count < MinReleaseDays)
        {
            return new ReleaseCadenceEstimate(
                CadenceState.Unknown, days.Count, null, null, times.Count > 0 ? times[^1] : (DateTime?)null,
                null, null, 0.5, 0, 0);
        }

        var (interval, spread, halfWidth) = Fit(days, days.Count);

        var hits = 0;
        var tested = 0;
        for (var i = Math.Max(MinReleaseDays, days.Count - Backtest); i < days.Count; i++)
        {
            var (m, _, w) = Fit(days, i);
            var predicted = days[i - 1].AddDays((int)Math.Round(m));
            tested++;
            if (Math.Abs(days[i].DayNumber - predicted.DayNumber) <= w)
            {
                hits++;
            }
        }

        var confidence = (hits + 1.0) / (tested + 2.0);
        var last = times[^1];
        // A window wider than two thirds of the cycle says no more than the cycle itself does.
        var state = interval > MaxIntervalDays || tested < MinChecked || confidence < MinConfidence ||
                    halfWidth > interval / 3
            ? CadenceState.Irregular
            : CadenceState.Expected;

        DateTime? start = null, end = null;
        if (state == CadenceState.Expected)
        {
            var expected = last.AddDays(Math.Round(interval));
            start = expected.AddDays(-halfWidth);
            end = expected.AddDays(halfWidth);
            var staleAfter = last.AddDays(2 * Math.Round(interval) + halfWidth);
            if (now > staleAfter)
            {
                state = CadenceState.Stalled;
                start = end = null;
            }
            else if (now > end)
            {
                state = CadenceState.Late;
            }
        }

        return new ReleaseCadenceEstimate(
            state, days.Count, interval, spread, last, start, end, confidence, hits, tested);
    }

    /// <summary>Median gap, its MAD and the window half-width, over the gaps before day <paramref name="end"/>.</summary>
    private static (double Interval, double Spread, int HalfWidth) Fit(List<DateOnly> days, int end)
    {
        var from = Math.Max(0, end - RecentGaps - 1);
        var gaps = new List<double>();
        for (var i = from + 1; i < end; i++)
        {
            gaps.Add(days[i].DayNumber - days[i - 1].DayNumber);
        }

        var interval = Median(gaps);
        var spread = Median(gaps.Select(g => Math.Abs(g - interval)).ToList());

        // A day either side at least; wider when the gaps wander or the cadence is long, so a
        // monthly series gets roughly a week rather than a day.
        var halfWidth = Math.Max(1, Math.Max((int)Math.Ceiling(spread), (int)(interval / 5)));
        return (interval, spread, halfWidth);
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }
}
