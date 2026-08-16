using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="DownloadBatchNotifier"/> turns a run of chapter downloads into two notifications
/// (queued + summary) and tells the caller when to stay quiet about an individual chapter.
/// </summary>
public class DownloadBatchNotifierTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly RecordingNotifications _notifications = new();
    private readonly RecordingInbox _inbox = new();
    private readonly StoppedClock _clock = new(T0);
    private readonly DownloadBatchNotifier _batches;

    public DownloadBatchNotifierTests() =>
        _batches = new DownloadBatchNotifier(
            _notifications, _inbox, _clock, NullLogger<DownloadBatchNotifier>.Instance);

    public void Dispose() => _batches.Dispose();

    private List<(NotificationEventType Type, NotificationMessage Message)> Sent => _notifications.Sent;

    [Fact]
    public void A_batch_announces_once_at_the_start_and_once_when_every_chapter_is_done()
    {
        _batches.Queued(1, "Berserk", [10, 11, 12]);

        Assert.Single(Sent);
        Assert.Equal("Downloads queued", Sent[0].Message.Title);
        Assert.Contains("3 chapter(s) queued", Sent[0].Message.Body);

        Assert.True(_batches.Completed(1, 10));
        Assert.True(_batches.Completed(1, 11));
        Assert.Single(Sent); // nothing per chapter

        Assert.True(_batches.Completed(1, 12));
        Assert.Equal(2, Sent.Count);
        Assert.Equal("Downloads complete", Sent[1].Message.Title);
        Assert.Equal(NotificationEventType.ChapterDownloaded, Sent[1].Type);
        Assert.Contains("3 of 3 chapter(s) downloaded", Sent[1].Message.Body);
    }

    [Fact]
    public void Failures_are_counted_into_the_summary_instead_of_pinging_per_chapter()
    {
        _batches.Queued(1, "Berserk", [10, 11]);
        Sent.Clear();

        Assert.True(_batches.Failed(1, 10, "Source returned no pages"));
        Assert.Empty(Sent);

        Assert.True(_batches.Completed(1, 11));
        var summary = Assert.Single(Sent);
        Assert.Equal(NotificationEventType.DownloadFailed, summary.Type);
        Assert.Equal(NotificationLevel.Warning, summary.Message.Level);
        Assert.Contains("1 downloaded, 1 failed of 2 queued", summary.Message.Body);
        Assert.Contains("Source returned no pages", summary.Message.Body);
    }

    [Fact]
    public void A_batch_where_everything_failed_is_an_error_not_a_warning()
    {
        _batches.Queued(1, "Berserk", [10, 11]);

        _batches.Failed(1, 10, "boom");
        _batches.Failed(1, 11, "boom");

        Assert.Equal(NotificationLevel.Error, Sent[^1].Message.Level);
    }

    [Fact]
    public void A_manual_run_reaches_the_connections_but_never_the_inbox()
    {
        // Somebody clicked "search missing". They watched the queue fill; telling them about it
        // afterwards is noise, and it is the whole reason the origin is recorded.
        _batches.Queued(1, "Berserk", [10, 11], DownloadOrigin.Manual);
        _batches.Completed(1, 10);
        _batches.Completed(1, 11);

        Assert.Equal(2, Sent.Count);
        Assert.Empty(_inbox.RaisedForSeries);
    }

    [Fact]
    public void An_untagged_run_is_treated_as_manual()
    {
        // Origin defaults to Unknown, which is also what pre-migration queue rows carry.
        _batches.Queued(1, "Berserk", [10, 11]);
        _batches.Completed(1, 10);
        _batches.Completed(1, 11);

        Assert.Empty(_inbox.RaisedForSeries);
    }

    [Fact]
    public void Smart_download_announces_the_queue_and_the_completion_in_the_inbox()
    {
        _batches.Queued(1, "Berserk", [10, 11], DownloadOrigin.SmartDownload);

        var queued = Assert.Single(_inbox.RaisedForSeries);
        Assert.Equal(InboxEventType.SmartDownloadQueued, queued.Type);
        Assert.Equal(1, queued.SeriesId);

        _batches.Completed(1, 10);
        _batches.Completed(1, 11);

        Assert.Equal(2, _inbox.RaisedForSeries.Count);
        Assert.Equal(InboxEventType.ChapterDownloaded, _inbox.RaisedForSeries[1].Type);
        Assert.Contains("2 chapter(s) ready to read", _inbox.RaisedForSeries[1].Message.Body);
    }

    [Fact]
    public void A_monitor_refresh_summarizes_without_a_second_queued_ping()
    {
        // The job already announced the new chapters under NewChapterAvailable, so the batch owes
        // only the summary — and SmartDownloadQueued must not fire for a run it did not start.
        _batches.Queued(1, "Berserk", [10, 11], DownloadOrigin.MonitorRefresh, announce: false);
        Assert.Empty(_inbox.RaisedForSeries);

        _batches.Completed(1, 10);
        _batches.Completed(1, 11);

        var summary = Assert.Single(_inbox.RaisedForSeries);
        Assert.Equal(InboxEventType.ChapterDownloaded, summary.Type);
    }

    [Fact]
    public void An_automatic_run_that_lost_chapters_raises_a_failure_in_the_inbox()
    {
        _batches.Queued(1, "Berserk", [10, 11], DownloadOrigin.RequestApproval);
        _inbox.RaisedForSeries.Clear();

        _batches.Failed(1, 10, "Source returned no pages");
        _batches.Completed(1, 11);

        var raised = Assert.Single(_inbox.RaisedForSeries);
        Assert.Equal(InboxEventType.DownloadFailed, raised.Type);
        Assert.Equal(NotificationLevel.Warning, raised.Message.Level);
        Assert.Contains("Source returned no pages", raised.Message.Body);
    }

    [Fact]
    public void A_single_chapter_opens_no_batch_so_it_still_notifies_per_chapter()
    {
        _batches.Queued(1, "Berserk", [10]);

        Assert.Empty(Sent);
        Assert.False(_batches.Completed(1, 10));
    }

    [Fact]
    public void Items_outside_any_batch_are_not_claimed()
    {
        _batches.Queued(1, "Berserk", [10, 11]);

        Assert.False(_batches.Completed(1, 99));   // same series, different run
        Assert.False(_batches.Completed(2, 10));   // another series entirely
    }

    [Fact]
    public void Chapters_added_mid_batch_join_it_without_a_second_queued_ping()
    {
        _batches.Queued(1, "Berserk", [10, 11]);
        _batches.Queued(1, "Berserk", [12]);
        Sent.Clear();

        _batches.Completed(1, 10);
        _batches.Completed(1, 11);
        Assert.Empty(Sent);

        _batches.Completed(1, 12);
        Assert.Contains("3 of 3", Assert.Single(Sent).Message.Body);
    }

    [Fact]
    public void A_caller_that_already_announced_the_count_still_gets_the_summary()
    {
        _batches.Queued(1, "Berserk", [10, 11], announce: false);
        Assert.Empty(Sent);

        _batches.Completed(1, 10);
        _batches.Completed(1, 11);
        Assert.Equal("Downloads complete", Assert.Single(Sent).Message.Title);
    }

    [Fact]
    public void A_cancelled_item_is_dropped_so_it_cannot_hold_the_batch_open()
    {
        _batches.Queued(1, "Berserk", [10, 11]);
        Sent.Clear();

        _batches.Completed(1, 10);
        _batches.Discard(1, 11);

        // Cancelling isn't an error, so the batch still settles as a plain completion — the
        // "of 2" is what shows the run didn't download everything it queued.
        var summary = Assert.Single(Sent);
        Assert.Equal(NotificationEventType.ChapterDownloaded, summary.Type);
        Assert.Contains("1 of 2 chapter(s) downloaded", summary.Message.Body);
    }

    [Fact]
    public void A_batch_that_stops_reporting_is_closed_by_the_sweep()
    {
        _batches.Queued(1, "Berserk", [10, 11]);
        _batches.Completed(1, 10);
        Sent.Clear();

        _clock.Now = T0.AddMinutes(59);
        _batches.SweepStale();
        Assert.Empty(Sent);

        _clock.Now = T0.AddHours(2);
        _batches.SweepStale();
        Assert.Contains("1 unfinished", Assert.Single(Sent).Message.Body);

        // The batch is gone, so the series notifies per chapter again rather than staying silent.
        Assert.False(_batches.Completed(1, 11));
    }
}
