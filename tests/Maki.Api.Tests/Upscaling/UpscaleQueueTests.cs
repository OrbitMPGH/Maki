using Maki.Api.Services.Upscaling;
using Maki.Core.Images.Upscaling;
using static Maki.Api.Tests.Upscaling.UpscaleTestKit;

namespace Maki.Api.Tests.Upscaling;

public class UpscaleQueueTests
{
    private readonly UpscaleQueue _queue = new();

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
        _queue.Replace(1, 1, "waifu2x-art", [Item(0, model: "waifu2x-art")]);
        _queue.Replace(1, 2, "waifu2x-art", [Item(7, chapterId: 2, fileId: 2, model: "waifu2x-art")]);

        var job = Assert.IsType<UpscaleJob>(_queue.TryDequeue());
        Assert.Equal(new UpscaleWorkKey(2, "1000-5", 7, "waifu2x-art"), job.Key);
        Assert.Null(_queue.TryDequeue());
        Assert.Equal(1, _queue.Snapshot().Working);
        Assert.Equal(0, _queue.Snapshot().Pending);
    }

    [Fact]
    public void Paging_on_in_one_chapter_keeps_older_pages_behind_the_new_window()
    {
        _queue.Replace(1, 1, "span-s", [Item(0), Item(1)]);
        _queue.Replace(1, 1, "span-s", [Item(5), Item(6)]);

        Assert.Equal([5, 6, 0, 1], DrainPages());
    }

    [Fact]
    public void A_reader_never_has_more_than_twelve_pages_pending()
    {
        _queue.Replace(1, 1, "span-s", Enumerable.Range(0, 12).Select(p => Item(p)).ToList());
        var states = _queue.Replace(1, 1, "span-s", Enumerable.Range(20, 15).Select(p => Item(p)).ToList());

        Assert.Equal(UpscaleQueue.MaxPendingPerUser, states.Count);
        Assert.Equal(UpscaleQueue.MaxPendingPerUser, _queue.PendingFor(1));
        Assert.Equal(UpscaleQueue.MaxPendingPerUser, _queue.Snapshot().Pending);
        Assert.Equal(Enumerable.Range(20, 12), DrainPages());
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
}
