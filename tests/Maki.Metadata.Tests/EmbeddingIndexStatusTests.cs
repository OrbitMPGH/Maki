using Maki.Metadata.Embedding;
using Xunit;

namespace Maki.Metadata.Tests;

public class EmbeddingIndexStatusTests
{
    private DateTime _now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private EmbeddingIndexStatus NewStatus() => new(() => _now);

    private void Advance(double seconds) => _now = _now.AddSeconds(seconds);

    [Fact]
    public void Eta_IsNullUntilTwoReportsGiveARate()
    {
        var status = NewStatus();
        status.Begin();
        status.SetTotal(1000);
        status.SetPhase("indexing");
        Assert.Null(status.Snapshot().EstimatedSecondsRemaining);

        status.Report(32, 32); // first report: no interval to measure yet
        Assert.Null(status.Snapshot().EstimatedSecondsRemaining);

        Advance(1);
        status.Report(64, 64);
        Assert.NotNull(status.Snapshot().EstimatedSecondsRemaining);
    }

    [Fact]
    public void Eta_IsRemainingRowsOverTheMeasuredRate()
    {
        var status = NewStatus();
        status.Begin();
        status.SetTotal(1000);
        status.SetPhase("indexing");

        // 32 rows per second, held steady.
        status.Report(32, 32);
        for (var scanned = 64; scanned <= 160; scanned += 32)
        {
            Advance(1);
            status.Report(scanned, scanned);
        }

        // 840 rows left at 32/s.
        Assert.Equal(26, status.Snapshot().EstimatedSecondsRemaining);
    }

    [Fact]
    public void Eta_TracksASlowdownRatherThanTheRunAverage()
    {
        var status = NewStatus();
        status.Begin();
        status.SetTotal(10_000);
        status.SetPhase("indexing");

        status.Report(32, 32);
        for (var scanned = 64; scanned <= 320; scanned += 32)
        {
            Advance(1); // fast: 32 rows/s
            status.Report(scanned, scanned);
        }

        var fast = status.Snapshot().EstimatedSecondsRemaining;

        for (var scanned = 352; scanned <= 640; scanned += 32)
        {
            Advance(8); // slow: 4 rows/s
            status.Report(scanned, scanned);
        }

        var slow = status.Snapshot().EstimatedSecondsRemaining;
        Assert.NotNull(fast);
        Assert.NotNull(slow);
        Assert.True(slow > fast * 3, $"expected the estimate to grow with the slowdown, got {fast} then {slow}");
    }

    [Fact]
    public void Eta_ChargesTimeSpentSinceTheLastReport()
    {
        var status = NewStatus();
        status.Begin();
        status.SetTotal(1000);
        status.SetPhase("indexing");
        status.Report(32, 32);
        Advance(1);
        status.Report(64, 64); // 32 rows/s, 936 left => ~29s

        var immediately = status.Snapshot().EstimatedSecondsRemaining;
        Advance(10); // a long run of skipped rows reports nothing
        var later = status.Snapshot().EstimatedSecondsRemaining;

        Assert.Equal(immediately - 10, later);
    }

    [Fact]
    public void Eta_IsNullWhenNotIndexing()
    {
        var status = NewStatus();
        status.Begin();
        status.SetTotal(1000);
        status.SetPhase("indexing");
        status.Report(32, 32);
        Advance(1);
        status.Report(64, 64);
        Assert.NotNull(status.Snapshot().EstimatedSecondsRemaining);

        status.SetPhase("preparing"); // e.g. a model download mid-pass
        Assert.Null(status.Snapshot().EstimatedSecondsRemaining);

        status.End(64, 0, null);
        Assert.Null(status.Snapshot().EstimatedSecondsRemaining);
    }

    [Fact]
    public void Eta_IsNullWithoutATotal()
    {
        var status = NewStatus();
        status.Begin();
        status.SetPhase("indexing");
        status.Report(32, 32);
        Advance(1);
        status.Report(64, 64);

        Assert.Null(status.Snapshot().EstimatedSecondsRemaining);
    }

    [Fact]
    public void Eta_PastTheTotal_IsZeroNotNegative()
    {
        var status = NewStatus();
        status.Begin();
        status.SetTotal(50);
        status.SetPhase("indexing");
        status.Report(32, 32);
        Advance(1);
        status.Report(64, 64); // the count was an estimate; the scan overshot it

        Assert.Equal(0, status.Snapshot().EstimatedSecondsRemaining);
    }

    [Fact]
    public void Begin_ResetsTheRateFromAPreviousRun()
    {
        var status = NewStatus();
        status.Begin();
        status.SetTotal(1000);
        status.SetPhase("indexing");
        status.Report(32, 32);
        Advance(1);
        status.Report(64, 64);
        status.End(64, 0, null);

        status.Begin();
        status.SetTotal(1000);
        status.SetPhase("indexing");
        Assert.Null(status.Snapshot().EstimatedSecondsRemaining);
    }
}
