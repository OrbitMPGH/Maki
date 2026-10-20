using Maki.Core.Releases;

namespace Maki.Core.Tests;

public class ReleaseCadenceTests
{
    private static readonly DateTime Start = new(2026, 6, 1, 15, 0, 0, DateTimeKind.Utc);

    private static List<DateTime> Every(int days, int count, DateTime? from = null) =>
        [.. Enumerable.Range(0, count).Select(i => (from ?? Start).AddDays(i * days))];

    [Fact]
    public void A_steady_weekly_series_gets_a_three_day_window_with_high_confidence()
    {
        var releases = Every(7, 12);
        var last = releases[^1];

        var estimate = ReleaseCadence.Estimate(releases, last.AddDays(2));

        Assert.Equal(CadenceState.Expected, estimate.State);
        Assert.Equal(7, estimate.Interval);
        Assert.Equal(0, estimate.Spread);
        Assert.Equal(last.AddDays(6), estimate.WindowStart);
        Assert.Equal(last.AddDays(8), estimate.WindowEnd);
        Assert.True(estimate.Confidence >= ReleaseCadence.HighConfidence);
        Assert.Equal(estimate.Checked, estimate.Hits);
    }

    [Fact]
    public void Too_few_release_days_is_unknown()
    {
        var estimate = ReleaseCadence.Estimate(Every(7, 3), Start.AddDays(20));

        Assert.Equal(CadenceState.Unknown, estimate.State);
        Assert.Null(estimate.WindowStart);
        Assert.Equal(Start.AddDays(14), estimate.LastRelease);
    }

    [Fact]
    public void A_same_day_batch_counts_as_one_release()
    {
        var releases = Every(7, 8).SelectMany(d => new[] { d, d.AddMinutes(3), d.AddHours(1) }).ToList();

        var estimate = ReleaseCadence.Estimate(releases, releases[^1].AddDays(1));

        Assert.Equal(8, estimate.ReleaseDays);
        Assert.Equal(7, estimate.Interval);
    }

    [Fact]
    public void A_series_whose_windows_keep_missing_is_irregular_and_draws_none()
    {
        int[] gaps = [3, 19, 6, 30, 2, 14, 41, 5, 9, 25, 4, 17];
        var releases = new List<DateTime> { Start };
        foreach (var gap in gaps)
        {
            releases.Add(releases[^1].AddDays(gap));
        }

        var estimate = ReleaseCadence.Estimate(releases, releases[^1].AddDays(1));

        Assert.Equal(CadenceState.Irregular, estimate.State);
        Assert.Null(estimate.WindowStart);
        Assert.NotNull(estimate.Interval);
        Assert.True(estimate.Confidence < ReleaseCadence.MinConfidence);
    }

    [Fact]
    public void A_window_wider_than_the_cycle_allows_is_not_drawn()
    {
        int[] gaps = [30, 12, 48, 30, 14, 46, 30, 13, 47, 30, 15, 45];
        var releases = new List<DateTime> { Start };
        foreach (var gap in gaps)
        {
            releases.Add(releases[^1].AddDays(gap));
        }

        var estimate = ReleaseCadence.Estimate(releases, releases[^1].AddDays(1));

        Assert.Equal(CadenceState.Irregular, estimate.State);
        Assert.Equal(30, estimate.Interval);
    }

    [Fact]
    public void Past_the_window_it_runs_late_and_then_stalls()
    {
        var releases = Every(7, 12);
        var last = releases[^1];

        Assert.Equal(CadenceState.Late, ReleaseCadence.Estimate(releases, last.AddDays(10)).State);

        var stalled = ReleaseCadence.Estimate(releases, last.AddDays(20));
        Assert.Equal(CadenceState.Stalled, stalled.State);
        Assert.Null(stalled.WindowEnd);
    }

    [Fact]
    public void A_monthly_series_gets_a_wider_window()
    {
        var releases = Every(30, 10);

        var estimate = ReleaseCadence.Estimate(releases, releases[^1].AddDays(1));

        Assert.Equal(releases[^1].AddDays(24), estimate.WindowStart);
        Assert.Equal(releases[^1].AddDays(36), estimate.WindowEnd);
    }

    [Fact]
    public void A_long_break_in_the_past_does_not_move_the_recent_rhythm()
    {
        var releases = Every(7, 6).Concat(Every(7, 9, Start.AddDays(200))).ToList();

        var estimate = ReleaseCadence.Estimate(releases, releases[^1].AddDays(1));

        Assert.Equal(7, estimate.Interval);
        Assert.Equal(CadenceState.Expected, estimate.State);
    }

    [Fact]
    public void Future_dated_chapters_are_ignored()
    {
        var releases = Every(7, 10);
        var now = releases[^1].AddDays(1);
        releases.Add(now.AddDays(30));

        Assert.Equal(releases[^2], ReleaseCadence.Estimate(releases, now).LastRelease);
    }
}
