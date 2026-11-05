using Maki.Api.Services;
using Maki.Core.Kavita;

namespace Maki.Api.Tests;

/// <summary>
/// Which Kavita series the scrobble tick fetches again while the live hub is connected: every one
/// on a full sweep, otherwise only those the hub reported, those never fetched and those whose
/// summary moved.
/// </summary>
public class KavitaLiveCoverageTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly Clock _clock = new();

    private static KavitaClient.KavitaSeriesSummary Series(int id, int pages = 100, int pagesRead = 10) =>
        new(id, $"Series {id}", null, 1, pages, pagesRead);

    private static List<KavitaProgress.KavitaVolumeDto> Volumes(int pagesRead) =>
        [new(1, 1, 100, pagesRead, [new(1, 1, 100, pagesRead, false)])];

    private KavitaLiveCoverage SweptCoverage(params KavitaClient.KavitaSeriesSummary[] series)
    {
        var coverage = new KavitaLiveCoverage(_clock);
        coverage.Connected();
        var pass = coverage.Begin();
        Assert.True(pass.Full);
        foreach (var s in series)
        {
            Assert.Null(pass.Reuse(s));
            pass.Fetched(s, Volumes(s.PagesRead));
        }

        pass.Complete();
        return coverage;
    }

    [Fact]
    public void Without_the_hub_every_pass_is_a_full_sweep()
    {
        var coverage = new KavitaLiveCoverage(_clock);
        var pass = coverage.Begin();
        pass.Fetched(Series(1), Volumes(10));
        pass.Complete();

        var next = coverage.Begin();

        Assert.True(next.Full);
        Assert.Null(next.Reuse(Series(1)));
    }

    [Fact]
    public void After_a_full_sweep_only_series_the_hub_reported_are_fetched()
    {
        var coverage = SweptCoverage(Series(1), Series(2));
        coverage.Saw(2);

        var pass = coverage.Begin();

        Assert.False(pass.Full);
        Assert.NotNull(pass.Reuse(Series(1)));
        Assert.Null(pass.Reuse(Series(2)));
    }

    [Fact]
    public void A_series_never_fetched_or_whose_summary_moved_is_fetched()
    {
        var coverage = SweptCoverage(Series(1));

        var pass = coverage.Begin();

        Assert.Null(pass.Reuse(Series(3)));
        Assert.Null(pass.Reuse(Series(1, pagesRead: 11)));
        Assert.Null(pass.Reuse(Series(1, pages: 120)));
        Assert.NotNull(pass.Reuse(Series(1)));
    }

    [Fact]
    public void A_reported_series_is_fetched_once_and_then_reused()
    {
        var coverage = SweptCoverage(Series(1));
        coverage.Saw(1);
        var first = coverage.Begin();
        Assert.Null(first.Reuse(Series(1)));
        first.Fetched(Series(1), Volumes(20));
        first.Complete();

        var second = coverage.Begin();

        Assert.Equal(20, second.Reuse(Series(1))![0].PagesRead);
    }

    [Fact]
    public void An_event_during_a_pass_is_kept_for_the_next_one()
    {
        var coverage = SweptCoverage(Series(1));
        var pass = coverage.Begin();
        Assert.NotNull(pass.Reuse(Series(1)));

        coverage.Saw(1);
        pass.Complete();

        Assert.Null(coverage.Begin().Reuse(Series(1)));
    }

    [Fact]
    public void A_failed_fetch_is_tried_again_on_the_next_pass()
    {
        var coverage = SweptCoverage(Series(1));
        coverage.Saw(1);
        var pass = coverage.Begin();
        pass.Failed(1);
        pass.Complete();

        Assert.Null(coverage.Begin().Reuse(Series(1)));
    }

    [Fact]
    public void A_reconnect_forces_a_full_sweep()
    {
        var coverage = SweptCoverage(Series(1));

        coverage.Disconnected();
        coverage.Connected();

        var pass = coverage.Begin();
        Assert.True(pass.Full);
        Assert.Null(pass.Reuse(Series(1)));
    }

    [Fact]
    public void A_drop_during_the_sweep_leaves_the_next_pass_full()
    {
        var coverage = new KavitaLiveCoverage(_clock);
        coverage.Connected();
        var pass = coverage.Begin();
        pass.Fetched(Series(1), Volumes(10));

        coverage.Disconnected();
        coverage.Connected();
        pass.Complete();

        Assert.True(coverage.Begin().Full);
    }

    [Fact]
    public void A_sweep_that_did_not_finish_is_run_again()
    {
        var coverage = new KavitaLiveCoverage(_clock);
        coverage.Connected();
        var pass = coverage.Begin();
        pass.Fetched(Series(1), Volumes(10));

        Assert.True(coverage.Begin().Full);
    }

    [Fact]
    public void The_full_sweep_comes_back_after_the_interval()
    {
        var coverage = SweptCoverage(Series(1));
        _clock.Now += KavitaLiveCoverage.FullSweepEvery - TimeSpan.FromMinutes(1);
        var pass = coverage.Begin();
        Assert.False(pass.Full);
        pass.Complete();

        _clock.Now += TimeSpan.FromMinutes(1);

        Assert.True(coverage.Begin().Full);
    }

    [Fact]
    public void A_partial_pass_that_does_not_finish_hands_its_dirty_series_to_the_next_pass()
    {
        var series = Series(1);
        var coverage = SweptCoverage(series);
        coverage.Saw(series.Id);

        var broken = coverage.Begin();
        Assert.False(broken.Full);
        Assert.Null(broken.Reuse(series));

        var next = coverage.Begin();
        Assert.False(next.Full);
        Assert.Null(next.Reuse(series));
        next.Complete();

        Assert.NotNull(coverage.Begin().Reuse(series));
    }
}
