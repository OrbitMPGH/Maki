using Maki.Api.Services.Upscaling;
using Maki.Core.Images.Upscaling;
using static Maki.Api.Tests.Upscaling.UpscaleTestKit;

namespace Maki.Api.Tests.Upscaling;

public class UpscaleQueueTests
{
    private readonly Clock _clock = new();
    private readonly UpscaleQueue _queue;

    public UpscaleQueueTests() => _queue = new UpscaleQueue(_clock);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private List<int> DrainPages()
    {
        var order = new List<int>();
        while (_queue.TryDequeue() is { } job)
        {
            order.Add(job.Key.AbsIndex);
            _queue.Complete(job, UpscaleOutcome.Ready, null);
        }

        return order;
    }

    [Fact]
    public void Two_readers_are_served_in_turn()
    {
        _queue.Replace(1, 1, "span-s", [Item(0), Item(1), Item(2)]);
        _queue.Replace(2, 2, "span-s", [Item(100, chapterId: 2, fileId: 2), Item(101, chapterId: 2, fileId: 2)]);

        Assert.Equal([0, 100, 1, 101, 2], DrainPages());
    }

    [Fact]
    public void A_page_two_readers_want_runs_once_and_answers_both()
    {
        _queue.Replace(1, 1, "span-s", [Item(4)]);
        var states = _queue.Replace(2, 1, "span-s", [Item(4)]);

        Assert.Equal([UpscaleJobState.Queued], states);
        var job = _queue.TryDequeue();
        Assert.NotNull(job);
        Assert.Null(_queue.TryDequeue());

        Assert.Equal([UpscaleJobState.Working], _queue.Replace(3, 1, "span-s", [Item(4)]));
        var waiters = _queue.Complete(job, UpscaleOutcome.Ready, null);
        Assert.Equal([1, 2, 3], waiters.Select(w => w.Key).Order());
    }

    [Fact]
    public void Replacing_drops_pending_pages_of_another_chapter_or_model()
    {
        _queue.Replace(1, 1, "span-s", [Item(0), Item(1)]);
        _queue.Replace(1, 1, "realesrgan-av3", [Item(0, model: "realesrgan-av3")]);
        _queue.Replace(1, 2, "realesrgan-av3", [Item(7, chapterId: 2, fileId: 2, model: "realesrgan-av3")]);

        var job = Assert.IsType<UpscaleJob>(_queue.TryDequeue());
        Assert.Equal(new UpscaleWorkKey(2, "1000-5", 7, "realesrgan-av3"), job.Key);
        Assert.Null(_queue.TryDequeue());
        Assert.Equal(1, _queue.Snapshot().Working);
        Assert.Equal(0, _queue.Snapshot().Pending);
    }

    [Fact]
    public void Paging_past_unfinished_pages_drops_them()
    {
        _queue.Replace(1, 1, "span-s", [Item(0), Item(1)]);
        _queue.Replace(1, 1, "span-s", [Item(5), Item(6)]);

        Assert.Equal([5, 6], DrainPages());
    }

    [Fact]
    public void Paging_back_keeps_older_pages_ahead_behind_the_new_window()
    {
        _queue.Replace(1, 1, "span-s", [Item(5), Item(6), Item(7), Item(8)]);
        _queue.Replace(1, 1, "span-s", [Item(4), Item(5)]);

        Assert.Equal([4, 5, 6, 7, 8], DrainPages());
    }

    [Fact]
    public void Moving_to_another_chapter_abandons_the_running_page()
    {
        _queue.Replace(1, 1, "span-s", [Item(0), Item(1)]);
        var running = _queue.TryDequeue()!;

        _queue.Replace(1, 2, "span-s", [Item(7, chapterId: 2, fileId: 2)]);

        Assert.True(running.Abandoned.IsCancellationRequested);
        Assert.Empty(_queue.Complete(running, UpscaleOutcome.Failed, UpscaleReasons.Abandoned));
        Assert.Null(_queue.RecentOutcome(running.Key));
        Assert.Equal([7], DrainPages());
    }

    [Fact]
    public void Paging_past_the_running_page_abandons_it()
    {
        _queue.Replace(1, 1, "span-s", [Item(0), Item(1), Item(2)]);
        var running = _queue.TryDequeue()!;

        _queue.Replace(1, 1, "span-s", [Item(2), Item(3)]);

        Assert.True(running.Abandoned.IsCancellationRequested);
        _queue.Complete(running, UpscaleOutcome.Failed, UpscaleReasons.Abandoned);
        Assert.Equal([2, 3], DrainPages());
    }

    [Fact]
    public void A_running_page_is_kept_while_it_is_still_ahead_or_another_reader_waits()
    {
        _queue.Replace(1, 1, "span-s", [Item(4)]);
        var running = _queue.TryDequeue()!;

        _queue.Replace(1, 1, "span-s", [Item(3), Item(5)]);
        Assert.False(running.Abandoned.IsCancellationRequested);
        _queue.Replace(1, 1, "span-s", []);
        Assert.False(running.Abandoned.IsCancellationRequested);
        _queue.Replace(1, 1, "span-s", [Item(4)]);

        _queue.Replace(2, 1, "span-s", [Item(4)]);
        _queue.Replace(1, 2, "realesrgan-av3", [Item(7, chapterId: 2, fileId: 2, model: "realesrgan-av3")]);
        Assert.False(running.Abandoned.IsCancellationRequested);
        Assert.Equal([2], _queue.Complete(running, UpscaleOutcome.Ready, null).Select(w => w.Key));
    }

    [Fact]
    public void An_abandoned_page_asked_for_again_is_queued_afresh()
    {
        _queue.Replace(1, 1, "span-s", [Item(0)]);
        var running = _queue.TryDequeue()!;
        _queue.Replace(1, 2, "span-s", [Item(7, chapterId: 2, fileId: 2)]);

        Assert.Equal([UpscaleJobState.Queued], _queue.Replace(1, 1, "span-s", [Item(0)]));
        _queue.Complete(running, UpscaleOutcome.Failed, UpscaleReasons.Abandoned);

        var again = _queue.TryDequeue()!;
        Assert.NotSame(running, again);
        Assert.Equal(running.Key, again.Key);
        Assert.False(again.Abandoned.IsCancellationRequested);
    }

    [Fact]
    public void A_reader_never_has_more_than_the_cap_pending()
    {
        var cap = UpscaleQueue.MaxPendingPerUser;
        _queue.Replace(1, 1, "span-s", Enumerable.Range(0, cap).Select(p => Item(p)).ToList());
        var states = _queue.Replace(1, 1, "span-s", Enumerable.Range(100, cap + 3).Select(p => Item(p)).ToList());

        Assert.Equal(UpscaleQueue.MaxPendingPerUser, states.Count);
        Assert.Equal(UpscaleQueue.MaxPendingPerUser, _queue.PendingFor(1));
        Assert.Equal(UpscaleQueue.MaxPendingPerUser, _queue.Snapshot().Pending);
        Assert.Equal(Enumerable.Range(100, cap), DrainPages());
    }

    [Fact]
    public void A_page_nobody_waits_on_any_more_never_starts()
    {
        _queue.Replace(1, 1, "span-s", [Item(0), Item(1)]);
        _queue.Replace(2, 1, "span-s", [Item(1)]);
        _queue.Replace(1, 9, "span-s", []);

        Assert.Equal([1], DrainPages());
        Assert.Equal(0, _queue.Snapshot().Pending);
    }

    [Fact]
    public void A_failed_page_is_remembered_and_a_model_reset_forgets_it()
    {
        _queue.Replace(1, 1, "span-s", [Item(3)]);
        var job = _queue.TryDequeue()!;
        _queue.Complete(job, UpscaleOutcome.Failed, UpscaleReasons.InferenceFailed);

        Assert.Equal(UpscaleOutcome.Failed, _queue.RecentOutcome(job.Key));
        _queue.ForgetModel("span-s");
        Assert.Null(_queue.RecentOutcome(job.Key));
    }

    [Fact]
    public void Remembering_forgetting_and_remembering_again_keeps_the_memo_order_bounded()
    {
        var page = new UpscaleJob(Item(0).Key, Slice());
        for (var i = 0; i < 5000; i++)
        {
            _queue.Complete(page, UpscaleOutcome.Failed, UpscaleReasons.InferenceFailed);
            _queue.Complete(page, UpscaleOutcome.Ready, null);
        }

        Assert.True(_queue.MemoOrderCount <= 600, $"{_queue.MemoOrderCount} order entries");

        _queue.Complete(page, UpscaleOutcome.Failed, UpscaleReasons.InferenceFailed);
        for (var i = 1; i < 1024; i++)
        {
            _queue.Complete(new UpscaleJob(Item(i).Key, Slice()), UpscaleOutcome.Skipped, UpscaleReasons.TooLarge);
            _queue.Complete(page, UpscaleOutcome.Failed, UpscaleReasons.InferenceFailed);
        }

        _queue.Complete(new UpscaleJob(Item(5000).Key, Slice()), UpscaleOutcome.Skipped, UpscaleReasons.TooLarge);

        Assert.Equal(UpscaleOutcome.Failed, _queue.RecentOutcome(Item(0).Key));
        Assert.Null(_queue.RecentOutcome(Item(1).Key));
        Assert.Equal(UpscaleOutcome.Skipped, _queue.RecentOutcome(Item(2).Key));
        Assert.True(_queue.MemoOrderCount <= 2 * 1024 + 1, $"{_queue.MemoOrderCount} order entries");
    }

    [Fact]
    public void A_whole_chapter_request_queues_past_the_window_cap()
    {
        var items = Enumerable.Range(0, 80).Select(page => Item(page)).ToList();

        Assert.Equal(UpscaleQueue.MaxPendingPerUser, _queue.Replace(1, 1, "span-s", items).Count);
        Assert.Equal(80, _queue.Replace(1, 1, "span-s", items, UpscaleQueue.MaxChapterPages).Count);
        Assert.Equal(Enumerable.Range(0, 80), DrainPages());
    }

    [Fact]
    public void Pages_shown_plain_are_dropped_from_what_an_earlier_request_queued()
    {
        _queue.Replace(1, 1, "span-s", [Item(0), Item(1), Item(2), Item(3)]);

        _queue.Replace(1, 1, "span-s", [], notWanted: new HashSet<UpscaleWorkKey> { Item(1).Key, Item(2).Key });

        Assert.Equal([0, 3], DrainPages());
    }

    [Fact]
    public void A_running_page_shown_plain_is_abandoned()
    {
        _queue.Replace(1, 1, "span-s", [Item(0), Item(1)]);
        var running = _queue.TryDequeue()!;

        _queue.Replace(1, 1, "span-s", [], notWanted: new HashSet<UpscaleWorkKey> { running.Key });

        Assert.True(running.Abandoned.IsCancellationRequested);
        Assert.Empty(_queue.Complete(running, UpscaleOutcome.Failed, UpscaleReasons.Abandoned));
        Assert.Equal([1], DrainPages());
    }

    [Fact]
    public void An_empty_request_from_another_chapter_ends_the_previous_chapters_pages()
    {
        _queue.Replace(1, 1, "span-s", [Item(4), Item(5), Item(0, chapterId: 2, fileId: 2)], UpscaleQueue.MaxChapterPages);

        _queue.Replace(1, 2, "span-s", []);

        Assert.Equal([0], DrainPages());
    }

    [Fact]
    public void Cancelling_drops_the_readers_lane_and_abandons_only_what_nobody_else_waits_on()
    {
        _queue.Replace(1, 1, "span-s", [Item(0), Item(1), Item(2)], UpscaleQueue.MaxChapterPages);
        _queue.Replace(2, 1, "span-s", [Item(1)]);
        var running = Assert.IsType<UpscaleJob>(_queue.TryDequeue());
        var shared = Assert.IsType<UpscaleJob>(_queue.TryDequeue());
        Assert.Equal(0, running.Key.AbsIndex);
        Assert.Equal(1, shared.Key.AbsIndex);

        _queue.Cancel(1);

        Assert.True(running.Abandoned.IsCancellationRequested);
        Assert.False(shared.Abandoned.IsCancellationRequested);
        Assert.Equal([2], _queue.Complete(shared, UpscaleOutcome.Ready, null).Select(w => w.Key));
        Assert.Null(_queue.TryDequeue());
        Assert.Equal(0, _queue.Snapshot().Pending);
    }

    [Fact]
    public void Two_tabs_on_one_account_keep_their_own_pages()
    {
        _queue.Replace(1, 1, "span-s", [Item(0), Item(1), Item(2)], session: "a");
        var running = _queue.TryDequeue()!;
        Assert.Equal(0, running.Key.AbsIndex);

        _queue.Replace(1, 2, "span-s", [Item(7, chapterId: 2, fileId: 2)], session: "b");
        Assert.False(running.Abandoned.IsCancellationRequested);
        Assert.Equal(2, _queue.PendingFor(1, "a"));

        _queue.Cancel(1, "b");
        Assert.False(running.Abandoned.IsCancellationRequested);
        Assert.Equal(2, _queue.PendingFor(1, "a"));
        Assert.Equal(0, _queue.PendingFor(1, "b"));

        _queue.Cancel(1);
        Assert.False(running.Abandoned.IsCancellationRequested);

        _queue.Cancel(1, "a");
        Assert.True(running.Abandoned.IsCancellationRequested);
        Assert.Equal(0, _queue.PendingFor(1, "a"));
    }

    [Fact]
    public void Two_tabs_waiting_on_one_page_are_told_once()
    {
        _queue.Replace(1, 1, "span-s", [Item(4)], session: "a");
        _queue.Replace(1, 1, "span-s", [Item(4)], session: "b");
        var job = _queue.TryDequeue()!;

        Assert.Equal([1], _queue.Complete(job, UpscaleOutcome.Ready, null).Select(w => w.Key));
    }

    [Fact]
    public void A_users_tabs_past_the_cap_share_one_lane_and_nothing_is_cancelled()
    {
        var running = new List<UpscaleJob>();
        for (var tab = 0; tab < UpscaleQueue.MaxSessionsPerUser; tab++)
        {
            _queue.Replace(1, 1, "span-s", [Item(tab * 10), Item(tab * 10 + 1)], session: $"tab{tab}");
            running.Add(_queue.TryDequeue()!);
        }

        _queue.Replace(1, 1, "span-s", [Item(100)], session: "extra1");
        _queue.Replace(1, 1, "span-s", [Item(200)], session: "extra2");
        _queue.Cancel(1, "extra1");

        Assert.All(running, job => Assert.False(job.Abandoned.IsCancellationRequested));
        Assert.All(Enumerable.Range(0, UpscaleQueue.MaxSessionsPerUser), tab => Assert.Equal(1, _queue.PendingFor(1, $"tab{tab}")));
        Assert.Equal(0, _queue.PendingFor(1, "extra1"));
        Assert.Equal(1, _queue.PendingFor(1));
        Assert.Equal(200, _queue.TryDequeue()!.Key.AbsIndex);
    }

    [Fact]
    public void A_tab_not_active_lately_only_runs_once_the_active_ones_are_empty()
    {
        _queue.Replace(1, 1, "span-s", [Item(0), Item(1)], UpscaleQueue.MaxChapterPages, session: "phone");
        _clock.Now += UpscaleQueue.ActiveWindow + TimeSpan.FromSeconds(1);
        _queue.Replace(1, 2, "span-s", [Item(50, chapterId: 2, fileId: 2), Item(51, chapterId: 2, fileId: 2)], session: "desktop");

        Assert.Equal([50, 51, 0, 1], DrainPages());
    }

    [Fact]
    public void A_hidden_tabs_request_does_not_make_it_active()
    {
        _queue.Replace(1, 1, "span-s", [Item(0), Item(1)], session: "phone");
        _clock.Now += UpscaleQueue.ActiveWindow + TimeSpan.FromSeconds(1);
        _queue.Replace(1, 2, "span-s", [Item(50, chapterId: 2, fileId: 2), Item(51, chapterId: 2, fileId: 2)], session: "desktop");
        _queue.Replace(1, 1, "span-s", [Item(0), Item(1)], session: "phone", visible: false);
        _queue.Replace(1, 1, "span-s", [Item(5)], session: "tablet", visible: false);

        Assert.Equal([50, 51, 0, 1, 5], DrainPages());
    }

    [Fact]
    public void Active_tabs_of_one_user_all_get_their_window_before_any_backlog()
    {
        _queue.Replace(1, 1, "span-s", [Item(0), Item(1), Item(10), Item(11)], UpscaleQueue.MaxChapterPages, session: "a", window: 2);
        _queue.Replace(1, 2, "span-s",
            [Item(50, chapterId: 2, fileId: 2), Item(51, chapterId: 2, fileId: 2), Item(60, chapterId: 2, fileId: 2)],
            UpscaleQueue.MaxChapterPages, session: "b", window: 2);

        Assert.Equal([0, 50, 1, 51, 10, 60, 11], DrainPages());
    }

    [Fact]
    public void A_folded_tab_that_last_filled_the_shared_lane_can_clear_it()
    {
        for (var tab = 0; tab < UpscaleQueue.MaxSessionsPerUser; tab++)
        {
            _queue.Replace(1, 1, "span-s", [Item(tab * 10)], session: $"tab{tab}");
        }

        _queue.Replace(1, 1, "span-s", [Item(100)], session: "extra1");
        _queue.Replace(1, 1, "span-s", [Item(200), Item(201)], UpscaleQueue.MaxChapterPages, session: "extra2");

        _queue.Cancel(1, "extra1");
        Assert.Equal(2, _queue.PendingFor(1));
        _queue.Cancel(1, "extra2");
        Assert.Equal(0, _queue.PendingFor(1));
        Assert.All(Enumerable.Range(0, UpscaleQueue.MaxSessionsPerUser), tab => Assert.Equal(1, _queue.PendingFor(1, $"tab{tab}")));
    }

    [Fact]
    public void Users_are_served_in_turn_however_many_tabs_they_have()
    {
        _queue.Replace(1, 1, "span-s", [Item(0), Item(1)], session: "a");
        _queue.Replace(1, 1, "span-s", [Item(10), Item(11)], session: "b");
        _queue.Replace(2, 2, "span-s", [Item(100, chapterId: 2, fileId: 2), Item(101, chapterId: 2, fileId: 2), Item(102, chapterId: 2, fileId: 2)]);

        Assert.Equal([0, 100, 10, 101, 1, 102, 11], DrainPages());
    }
}
