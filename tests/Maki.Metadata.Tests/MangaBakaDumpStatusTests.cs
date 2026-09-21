using Maki.Metadata.MangaBaka;
using Xunit;

namespace Maki.Metadata.Tests;

public class MangaBakaDumpStatusTests
{
    private DateTime _now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private MangaBakaDumpStatus NewStatus() => new(() => _now);

    private void Advance(double seconds) => _now = _now.AddSeconds(seconds);

    [Fact]
    public void Begin_entersCheckingRatherThanDownloading()
    {
        var status = NewStatus();
        status.Begin();

        // The UI shows no bar for "checking": it is one small checksum request, and it runs on
        // every six-hourly tick whether or not there is anything to download.
        var snapshot = status.Snapshot();
        Assert.True(snapshot.Running);
        Assert.Equal("checking", snapshot.Phase);
        Assert.Equal(0, snapshot.DownloadedBytes);
        Assert.Null(snapshot.TotalBytes);
    }

    [Fact]
    public void Progress_tracksBytesAgainstTheReportedTotal()
    {
        var status = NewStatus();
        status.Begin();
        status.BeginDownload(1000);

        Advance(1);
        status.ReportDownloaded(250);

        var snapshot = status.Snapshot();
        Assert.Equal("downloading", snapshot.Phase);
        Assert.Equal(250, snapshot.DownloadedBytes);
        Assert.Equal(1000, snapshot.TotalBytes);
    }

    [Fact]
    public void Reports_closerTogetherThanHalfASecondStillMoveTheByteCount()
    {
        var status = NewStatus();
        status.Begin();
        status.BeginDownload(1000);

        // The callback sits on the read path, so most calls land inside the throttle window. They
        // must still update the count, or the bar would freeze between rate samples.
        Advance(0.1);
        status.ReportDownloaded(80);
        Assert.Equal(80, status.Snapshot().DownloadedBytes);
        Assert.Null(status.Snapshot().BytesPerSecond);
    }

    [Fact]
    public void Eta_isRemainingBytesOverTheMeasuredRate()
    {
        var status = NewStatus();
        status.Begin();
        status.BeginDownload(1000);

        // 100 bytes per second, held steady.
        for (var sent = 100; sent <= 200; sent += 100)
        {
            Advance(1);
            status.ReportDownloaded(sent);
        }

        // 800 bytes left at 100/s.
        var snapshot = status.Snapshot();
        Assert.Equal(100, snapshot.BytesPerSecond);
        Assert.Equal(8, snapshot.EstimatedSecondsRemaining);
    }

    [Fact]
    public void Eta_isNullWithoutATotalOrOutsideTheDownload()
    {
        var status = NewStatus();
        status.Begin();
        status.BeginDownload(null);
        Advance(1);
        status.ReportDownloaded(100);
        Advance(1);
        status.ReportDownloaded(200);
        Assert.Null(status.Snapshot().EstimatedSecondsRemaining);

        // The index build that follows has no measurable unit of work, so carrying the download's
        // countdown into it would show a timer that hit zero and stopped.
        status.SetPhase("indexing");
        Assert.Null(status.Snapshot().EstimatedSecondsRemaining);
    }

    [Fact]
    public void End_clearsRunningAndKeepsTheOutcome()
    {
        var status = NewStatus();
        status.Begin();
        status.BeginDownload(1000);
        status.End(installed: false, error: "boom");

        var snapshot = status.Snapshot();
        Assert.False(snapshot.Running);
        Assert.Equal("idle", snapshot.Phase);
        Assert.Equal("boom", snapshot.LastError);
        Assert.False(snapshot.LastInstalled);
        Assert.NotNull(snapshot.FinishedAt);

        // A second run starts clean, so a stale error can't keep a red card up after it succeeds.
        status.Begin();
        Assert.Null(status.Snapshot().LastError);
    }
}
