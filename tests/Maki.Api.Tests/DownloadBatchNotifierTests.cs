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
            _notifications,
            _inbox,
            new TestLocalizer(),
            new TestUserLocaleResolver(),
            _clock,
            NullLogger<DownloadBatchNotifier>.Instance);

    public void Dispose() => _batches.Dispose();

    private List<(NotificationEventType Type, NotificationMessage Message)> Sent => _notifications.Sent;

    [Fact]
    public async Task A_batch_announces_once_at_the_start_and_once_when_every_chapter_is_done()
    {
        await _batches.QueuedAsync(1, "Berserk", [10, 11, 12]);

        Assert.Single(Sent);
        Assert.Equal("en:notify.downloads.queued.title", Sent[0].Message.Title);
        Assert.Contains("notify.downloads.queued.body", Sent[0].Message.Body);
        Assert.Contains("count=3", Sent[0].Message.Body);

        Assert.True(await _batches.CompletedAsync(1, 10));
        Assert.True(await _batches.CompletedAsync(1, 11));
        Assert.Single(Sent); // nothing per chapter

        Assert.True(await _batches.CompletedAsync(1, 12));
        Assert.Equal(2, Sent.Count);
        Assert.Equal("en:notify.downloads.complete.title", Sent[1].Message.Title);
        Assert.Equal(NotificationEventType.ChapterDownloaded, Sent[1].Type);
        Assert.Contains("count=3", Sent[1].Message.Body);
        Assert.Contains("queued=3", Sent[1].Message.Body);
    }

    [Fact]
    public async Task Failures_are_counted_into_the_summary_instead_of_pinging_per_chapter()
    {
        await _batches.QueuedAsync(1, "Berserk", [10, 11]);
        Sent.Clear();

        Assert.True(await _batches.FailedAsync(1, 10, "error.download.noPages"));
        Assert.Empty(Sent);

        Assert.True(await _batches.CompletedAsync(1, 11));
        var summary = Assert.Single(Sent);
        Assert.Equal(NotificationEventType.DownloadFailed, summary.Type);
        Assert.Equal(NotificationLevel.Warning, summary.Message.Level);
        Assert.Contains("completed=1", summary.Message.Body);
        Assert.Contains("failed=1", summary.Message.Body);
        Assert.Contains("queued=2", summary.Message.Body);
        Assert.Contains("error.download.noPages", summary.Message.Body);
    }

    [Fact]
    public async Task A_batch_where_everything_failed_is_an_error_not_a_warning()
    {
        await _batches.QueuedAsync(1, "Berserk", [10, 11]);

        await _batches.FailedAsync(1, 10, "error.download.unexpected");
        await _batches.FailedAsync(1, 11, "error.download.unexpected");

        Assert.Equal(NotificationLevel.Error, Sent[^1].Message.Level);
    }

    [Fact]
    public async Task A_manual_run_reaches_the_connections_but_never_the_inbox()
    {
        // Somebody clicked "search missing". They watched the queue fill; telling them about it
        // afterwards is noise, and it is the whole reason the origin is recorded.
        await _batches.QueuedAsync(1, "Berserk", [10, 11], DownloadOrigin.Manual);
        await _batches.CompletedAsync(1, 10);
        await _batches.CompletedAsync(1, 11);

        Assert.Equal(2, Sent.Count);
        Assert.Empty(_inbox.RaisedForSeries);
    }

    [Fact]
    public async Task An_untagged_run_is_treated_as_manual()
    {
        // Origin defaults to Unknown, which is also what pre-migration queue rows carry.
        await _batches.QueuedAsync(1, "Berserk", [10, 11]);
        await _batches.CompletedAsync(1, 10);
        await _batches.CompletedAsync(1, 11);

        Assert.Empty(_inbox.RaisedForSeries);
    }

    [Fact]
    public async Task Smart_download_announces_the_queue_and_the_completion_in_the_inbox()
    {
        await _batches.QueuedAsync(1, "Berserk", [10, 11], DownloadOrigin.SmartDownload);

        var queued = Assert.Single(_inbox.RaisedForSeries);
        Assert.Equal(InboxEventType.SmartDownloadQueued, queued.Type);
        Assert.Equal(1, queued.SeriesId);

        await _batches.CompletedAsync(1, 10);
        await _batches.CompletedAsync(1, 11);

        Assert.Equal(2, _inbox.RaisedForSeries.Count);
        Assert.Equal(InboxEventType.ChapterDownloaded, _inbox.RaisedForSeries[1].Type);
        Assert.Equal("inbox.chapters.downloaded", _inbox.RaisedForSeries[1].Message.Key);
        Assert.Equal(2, _inbox.RaisedForSeries[1].Message.Params?["count"]);
    }

    [Fact]
    public async Task A_monitor_refresh_summarizes_without_a_second_queued_ping()
    {
        // The job already announced the new chapters under NewChapterAvailable, so the batch owes
        // only the summary — and SmartDownloadQueued must not fire for a run it did not start.
        await _batches.QueuedAsync(1, "Berserk", [10, 11], DownloadOrigin.MonitorRefresh, announce: false);
        Assert.Empty(_inbox.RaisedForSeries);

        await _batches.CompletedAsync(1, 10);
        await _batches.CompletedAsync(1, 11);

        var summary = Assert.Single(_inbox.RaisedForSeries);
        Assert.Equal(InboxEventType.ChapterDownloaded, summary.Type);
    }

    [Fact]
    public async Task An_automatic_run_that_lost_chapters_raises_a_failure_in_the_inbox()
    {
        await _batches.QueuedAsync(1, "Berserk", [10, 11], DownloadOrigin.RequestApproval);
        _inbox.RaisedForSeries.Clear();

        await _batches.FailedAsync(1, 10, "error.download.noPages");
        await _batches.CompletedAsync(1, 11);

        var raised = Assert.Single(_inbox.RaisedForSeries);
        Assert.Equal(InboxEventType.DownloadFailed, raised.Type);
        Assert.Equal(NotificationLevel.Warning, raised.Message.Level);
        Assert.Equal("inbox.downloads.finishedWithErrors", raised.Message.Key);
        // The key, not a sentence: the inbox renders it per reader, in their own language.
        Assert.Equal("error.download.noPages", raised.Message.Params?["error"]);
    }

    [Fact]
    public async Task A_single_chapter_opens_no_batch_so_it_still_notifies_per_chapter()
    {
        await _batches.QueuedAsync(1, "Berserk", [10]);

        Assert.Empty(Sent);
        Assert.False(await _batches.CompletedAsync(1, 10));
    }

    [Fact]
    public async Task Items_outside_any_batch_are_not_claimed()
    {
        await _batches.QueuedAsync(1, "Berserk", [10, 11]);

        Assert.False(await _batches.CompletedAsync(1, 99));   // same series, different run
        Assert.False(await _batches.CompletedAsync(2, 10));   // another series entirely
    }

    [Fact]
    public async Task Chapters_added_mid_batch_join_it_without_a_second_queued_ping()
    {
        await _batches.QueuedAsync(1, "Berserk", [10, 11]);
        await _batches.QueuedAsync(1, "Berserk", [12]);
        Sent.Clear();

        await _batches.CompletedAsync(1, 10);
        await _batches.CompletedAsync(1, 11);
        Assert.Empty(Sent);

        await _batches.CompletedAsync(1, 12);
        Assert.Contains("count=3", Assert.Single(Sent).Message.Body);
    }

    [Fact]
    public async Task A_caller_that_already_announced_the_count_still_gets_the_summary()
    {
        await _batches.QueuedAsync(1, "Berserk", [10, 11], announce: false);
        Assert.Empty(Sent);

        await _batches.CompletedAsync(1, 10);
        await _batches.CompletedAsync(1, 11);
        Assert.Equal("en:notify.downloads.complete.title", Assert.Single(Sent).Message.Title);
    }

    [Fact]
    public async Task A_cancelled_item_is_dropped_so_it_cannot_hold_the_batch_open()
    {
        await _batches.QueuedAsync(1, "Berserk", [10, 11]);
        Sent.Clear();

        await _batches.CompletedAsync(1, 10);
        await _batches.DiscardAsync(1, 11);

        // Cancelling isn't an error, so the batch still settles as a plain completion — the
        // "of 2" is what shows the run didn't download everything it queued.
        var summary = Assert.Single(Sent);
        Assert.Equal(NotificationEventType.ChapterDownloaded, summary.Type);
        Assert.Contains("count=1", summary.Message.Body);
        Assert.Contains("queued=2", summary.Message.Body);
    }

    [Fact]
    public async Task A_batch_that_stops_reporting_is_closed_by_the_sweep()
    {
        await _batches.QueuedAsync(1, "Berserk", [10, 11]);
        await _batches.CompletedAsync(1, 10);
        Sent.Clear();

        _clock.Now = T0.AddMinutes(59);
        await _batches.SweepStaleAsync();
        Assert.Empty(Sent);

        _clock.Now = T0.AddHours(2);
        await _batches.SweepStaleAsync();
        Assert.Contains("unfinished=1", Assert.Single(Sent).Message.Body);

        // The batch is gone, so the series notifies per chapter again rather than staying silent.
        Assert.False(await _batches.CompletedAsync(1, 11));
    }
}
