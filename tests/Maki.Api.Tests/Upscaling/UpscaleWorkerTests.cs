using System.Collections.Concurrent;
using System.Diagnostics;
using Maki.Api.Configuration;
using Maki.Api.Services;
using Maki.Api.Services.Upscaling;
using Maki.Core.Configuration;
using Maki.Core.Images.Upscaling;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests.Upscaling;

public sealed class UpscaleWorkerTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "maki-upscale-worker-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly FakeAppSettings _settings = new FakeAppSettings()
        .Set(SettingKeys.UpscalingEnabled, "true")
        .Set(SettingKeys.UpscalingModels, "span-s")
        .Set(SettingKeys.UpscalingThreads, "2");
    private readonly UpscaleQueue _queue = new();
    private readonly RecordingBroadcaster _broadcaster = new();
    private readonly UpscaleCache _cache;
    private readonly UpscaleModelCatalog _catalog = new();
    private readonly UpscaleModelStore _store;
    private readonly string _cbz;

    public UpscaleWorkerTests()
    {
        _paths = new AppPaths(_configDir);
        _cache = new UpscaleCache(_paths, _settings, NullLogger<UpscaleCache>.Instance);
        _store = UpscaleTestKit.StoreWithSpanS(_paths, _settings, _catalog);
        _cbz = Path.Combine(_configDir, "library", "c1.cbz");
        UpscaleTestKit.WriteCbz(_cbz, pages: 5);
    }

    public void Dispose() => UpscaleTestKit.TryDelete(_configDir);

    private UpscaleWorker NewWorker(IPageUpscaler upscaler) => new(
        _queue, _cache, upscaler, _store, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance), _paths, _settings,
        _broadcaster, NullLogger<UpscaleWorker>.Instance);

    private ReaderService.PageSlice Slice() =>
        new(7, 3, _cbz, new FileInfo(_cbz).Length, ["001.png", "002.png", "003.png", "004.png", "005.png"], 0, 5, 99);

    private UpscaleRequestItem Item(int page) =>
        new(new UpscaleWorkKey(3, Slice().ArchiveVersion, page, "span-s"), new UpscaleTarget(7, page), Slice());

    /// <summary>Runs one job through both stages.</summary>
    private static async Task<bool> RunJobAsync(UpscaleWorker worker)
    {
        var ran = await worker.RunOnceAsync(default);
        await worker.Finishing;
        return ran;
    }

    private async Task WaitForSentAsync(int count)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (_broadcaster.Sent.Count < count && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(_broadcaster.Sent.Count >= count, $"Only {_broadcaster.Sent.Count} of {count} pushes arrived.");
    }

    [Fact]
    public async Task A_job_runs_the_model_writes_the_cache_and_tells_every_waiting_reader()
    {
        using var upscaler = new OnnxPageUpscaler(_catalog, _store, _settings, NullLogger<OnnxPageUpscaler>.Instance);
        _queue.Replace(1, 7, "span-s", [Item(1)]);
        _queue.Replace(2, 7, "span-s", [Item(1)]);

        Assert.True(await RunJobAsync(NewWorker(upscaler)));

        var cached = _cache.TryGet(Item(1).Key);
        Assert.NotNull(cached);
        Assert.EndsWith(".span-s.g6.webp", cached);
        Assert.Equal(
            [(1, 7, 1, "span-s", "working"), (1, 7, 1, "span-s", "ready"), (2, 7, 1, "span-s", "working"), (2, 7, 1, "span-s", "ready")],
            _broadcaster.Sent.OrderBy(s => s.UserId));
        Assert.Equal(0, _queue.Snapshot().Pending + _queue.Snapshot().Working);
    }

    [Fact]
    public async Task A_throwing_upscaler_fails_that_page_and_the_loop_keeps_going()
    {
        var upscaler = new FakeUpscaler();
        upscaler.ThrowFor.Add(1);
        _queue.Replace(1, 7, "span-s", [Item(0), Item(2)]);
        var worker = NewWorker(upscaler);

        await worker.StartAsync(default);
        try
        {
            await WaitForSentAsync(4);
        }
        finally
        {
            await worker.StopAsync(default);
        }

        var sent = _broadcaster.Sent;
        Assert.Equal([(1, 7, 0, "span-s", "working"), (1, 7, 0, "span-s", "failed")], sent.Where(s => s.Page == 0));
        Assert.Equal([(1, 7, 2, "span-s", "working"), (1, 7, 2, "span-s", "ready")], sent.Where(s => s.Page == 2));
        Assert.Equal(UpscaleOutcome.Failed, _queue.RecentOutcome(Item(0).Key));
        Assert.NotNull(_cache.TryGet(Item(2).Key));
    }

    [Fact]
    public async Task With_the_tier_off_nothing_runs_and_pending_work_is_dropped()
    {
        _settings.Set(SettingKeys.UpscalingEnabled, "false");
        var upscaler = new FakeUpscaler();
        _queue.Replace(1, 7, "span-s", [Item(0)]);

        Assert.False(await RunJobAsync(NewWorker(upscaler)));

        Assert.Equal(0, upscaler.Calls);
        Assert.Equal(0, _queue.Snapshot().Pending);
    }

    [Fact]
    public async Task A_page_for_a_model_the_admin_unticked_fails_without_running()
    {
        _settings.Set(SettingKeys.UpscalingModels, "realesrgan-av3");
        var upscaler = new FakeUpscaler();
        _queue.Replace(1, 7, "span-s", [Item(0)]);

        Assert.True(await RunJobAsync(NewWorker(upscaler)));

        Assert.Equal(0, upscaler.Calls);
        Assert.Equal([(1, 7, 0, "span-s", "failed")], _broadcaster.Sent);
        Assert.Null(_queue.RecentOutcome(Item(0).Key));
    }

    [Fact]
    public async Task A_model_deleted_while_its_page_ran_leaves_nothing_in_the_cache()
    {
        var upscaler = new DeletingUpscaler(_store);
        _queue.Replace(1, 7, "span-s", [Item(0)]);

        Assert.True(await RunJobAsync(NewWorker(upscaler)));

        Assert.Null(_cache.TryGet(Item(0).Key));
        Assert.Equal([(1, 7, 0, "span-s", "working"), (1, 7, 0, "span-s", "failed")], _broadcaster.Sent);
        Assert.Null(_queue.RecentOutcome(Item(0).Key));
    }

    [Fact]
    public async Task A_model_deleted_while_its_page_finishes_leaves_nothing_in_the_cache()
    {
        var upscaler = new StagedUpscaler();
        _queue.Replace(1, 7, "span-s", [Item(0)]);
        var worker = NewWorker(upscaler);

        Assert.True(await worker.RunOnceAsync(default));
        await upscaler.Finishing(0).Task.WaitAsync(Wait);
        await _store.DeleteAsync(UpscaleModelId.SpanS);
        upscaler.Release(0);
        await worker.Finishing.WaitAsync(Wait);

        Assert.Null(_cache.TryGet(Item(0).Key));
        Assert.Equal([(1, 7, 0, "span-s", "working"), (1, 7, 0, "span-s", "failed")], _broadcaster.Sent);
        Assert.Null(_queue.RecentOutcome(Item(0).Key));
    }

    [Fact]
    public async Task A_page_that_could_not_be_cached_is_not_remembered_as_failed()
    {
        Directory.CreateDirectory(_cache.Root);
        await File.WriteAllBytesAsync(Path.Combine(_cache.Root, "3"), [1]);
        _queue.Replace(1, 7, "span-s", [Item(0)]);

        Assert.True(await RunJobAsync(NewWorker(new FakeUpscaler())));

        Assert.Equal([(1, 7, 0, "span-s", "working"), (1, 7, 0, "span-s", "failed")], _broadcaster.Sent);
        Assert.Null(_queue.RecentOutcome(Item(0).Key));
    }

    [Fact]
    public async Task A_page_abandoned_mid_run_stops_and_is_not_remembered_or_cached()
    {
        var upscaler = new BlockingUpscaler();
        _queue.Replace(1, 7, "span-s", [Item(0)]);
        var worker = NewWorker(upscaler);
        var run = worker.RunOnceAsync(default);
        await upscaler.Started.Task.WaitAsync(Wait);

        _queue.Replace(1, 8, "span-s", []);

        Assert.True(await run.WaitAsync(Wait));
        await worker.Finishing.WaitAsync(Wait);
        Assert.Null(_cache.TryGet(Item(0).Key));
        Assert.Null(_queue.RecentOutcome(Item(0).Key));
        Assert.Equal([(1, 7, 0, "span-s", "working")], _broadcaster.Sent);
    }

    [Fact]
    public async Task A_page_already_in_the_cache_is_answered_ready_without_running()
    {
        await _cache.WriteAsync(Item(1).Key, [1, 2, 3], "webp", default);
        var upscaler = new FakeUpscaler();
        _queue.Replace(1, 7, "span-s", [Item(1)]);

        Assert.True(await RunJobAsync(NewWorker(upscaler)));

        Assert.Equal(0, upscaler.Calls);
        Assert.Equal([(1, 7, 1, "span-s", "ready")], _broadcaster.Sent);
        Assert.Equal(0, _queue.Snapshot().Pending + _queue.Snapshot().Working);
    }

    [Fact]
    public async Task The_next_page_runs_its_tiles_while_the_previous_one_finishes_and_only_one_finishes_at_a_time()
    {
        var upscaler = new StagedUpscaler();
        var readyWithoutCache = new ConcurrentBag<int>();
        _broadcaster.OnSent = s =>
        {
            if (s.State == "ready" && _cache.TryGet(Item(s.Page).Key) is null)
            {
                readyWithoutCache.Add(s.Page);
            }
        };
        _queue.Replace(1, 7, "span-s", [Item(0), Item(1), Item(2)]);
        var worker = NewWorker(upscaler);

        await worker.StartAsync(default);
        try
        {
            await upscaler.Finishing(0).Task.WaitAsync(Wait);
            await upscaler.Started(1).Task.WaitAsync(Wait);
            await Task.Delay(200);
            Assert.False(upscaler.Finishing(1).Task.IsCompleted);
            Assert.False(upscaler.Started(2).Task.IsCompleted);
            Assert.Null(_cache.TryGet(Item(0).Key));

            upscaler.Release(0);
            await upscaler.Finishing(1).Task.WaitAsync(Wait);
            await upscaler.Started(2).Task.WaitAsync(Wait);
            Assert.NotNull(_cache.TryGet(Item(0).Key));
            await Task.Delay(100);
            Assert.False(upscaler.Finishing(2).Task.IsCompleted);

            upscaler.Release(1);
            upscaler.Release(2);
            await WaitForSentAsync(6);
        }
        finally
        {
            await worker.StopAsync(default);
        }

        Assert.Equal(
            [
                (1, 7, 0, "span-s", "working"), (1, 7, 1, "span-s", "working"), (1, 7, 0, "span-s", "ready"),
                (1, 7, 2, "span-s", "working"), (1, 7, 1, "span-s", "ready"), (1, 7, 2, "span-s", "ready")
            ],
            _broadcaster.Sent);
        Assert.Empty(readyWithoutCache);
        Assert.Equal(1, upscaler.MaxFinishing);
        Assert.Equal(0, _queue.Snapshot().Pending + _queue.Snapshot().Working);
    }

    [Fact]
    public async Task A_burst_of_pages_finishes_one_at_a_time_in_order()
    {
        var upscaler = new StagedUpscaler { FinishDelay = TimeSpan.FromMilliseconds(30) };
        for (var i = 0; i < 5; i++)
        {
            upscaler.Release(i);
        }

        _queue.Replace(1, 7, "span-s", Enumerable.Range(0, 5).Select(Item).ToList());
        var worker = NewWorker(upscaler);

        await worker.StartAsync(default);
        try
        {
            await WaitForSentAsync(10);
        }
        finally
        {
            await worker.StopAsync(default);
        }

        Assert.Equal(1, upscaler.MaxFinishing);
        Assert.Equal([0, 1, 2, 3, 4], _broadcaster.Sent.Where(s => s.State == "ready").Select(s => s.Page));
        Assert.All(Enumerable.Range(0, 5), p => Assert.NotNull(_cache.TryGet(Item(p).Key)));
    }

    [Fact]
    public async Task A_page_abandoned_while_it_finishes_is_still_cached()
    {
        var upscaler = new StagedUpscaler();
        _queue.Replace(1, 7, "span-s", [Item(0)]);
        var worker = NewWorker(upscaler);

        Assert.True(await worker.RunOnceAsync(default));
        await upscaler.Finishing(0).Task.WaitAsync(Wait);
        _queue.Replace(1, 8, "span-s", []);
        upscaler.Release(0);
        await worker.Finishing.WaitAsync(Wait);

        Assert.NotNull(_cache.TryGet(Item(0).Key));
        Assert.Null(_queue.RecentOutcome(Item(0).Key));
        Assert.Equal([(1, 7, 0, "span-s", "working")], _broadcaster.Sent);
        Assert.Equal(0, _queue.Snapshot().Working);
    }

    [Fact]
    public async Task A_page_asked_for_again_while_its_abandoned_run_finishes_is_served_from_the_cache()
    {
        var upscaler = new StagedUpscaler();
        _queue.Replace(1, 7, "span-s", [Item(0)]);
        var worker = NewWorker(upscaler);

        Assert.True(await worker.RunOnceAsync(default));
        await upscaler.Finishing(0).Task.WaitAsync(Wait);
        _queue.Replace(1, 8, "span-s", []);
        _queue.Replace(1, 7, "span-s", [Item(0)]);
        var again = worker.RunOnceAsync(default);
        await Task.Delay(100);
        Assert.False(again.IsCompleted);

        upscaler.Release(0);
        Assert.True(await again.WaitAsync(Wait));
        await worker.Finishing.WaitAsync(Wait);

        Assert.Equal(1, upscaler.Calls);
        Assert.Equal([(1, 7, 0, "span-s", "working"), (1, 7, 0, "span-s", "ready")], _broadcaster.Sent);
        Assert.Equal(0, _queue.Snapshot().Pending + _queue.Snapshot().Working);
    }

    [Fact]
    public async Task A_failure_while_finishing_fails_that_page_and_the_next_one_still_runs()
    {
        var upscaler = new StagedUpscaler { ThrowWhileFinishing = 0 };
        upscaler.Release(0);
        upscaler.Release(1);
        _queue.Replace(1, 7, "span-s", [Item(0), Item(1)]);
        var worker = NewWorker(upscaler);

        await worker.StartAsync(default);
        try
        {
            await WaitForSentAsync(4);
        }
        finally
        {
            await worker.StopAsync(default);
        }

        var sent = _broadcaster.Sent;
        Assert.Equal([(1, 7, 0, "span-s", "working"), (1, 7, 0, "span-s", "failed")], sent.Where(s => s.Page == 0));
        Assert.Equal([(1, 7, 1, "span-s", "working"), (1, 7, 1, "span-s", "ready")], sent.Where(s => s.Page == 1));
        Assert.Equal(UpscaleOutcome.Failed, _queue.RecentOutcome(Item(0).Key));
        Assert.Null(_cache.TryGet(Item(0).Key));
        Assert.NotNull(_cache.TryGet(Item(1).Key));
    }

    [Fact]
    public async Task Stopping_while_a_page_finishes_ends_it_without_caching()
    {
        var upscaler = new StagedUpscaler();
        _queue.Replace(1, 7, "span-s", [Item(0)]);
        var worker = NewWorker(upscaler);

        await worker.StartAsync(default);
        await upscaler.Finishing(0).Task.WaitAsync(Wait);
        await worker.StopAsync(default).WaitAsync(Wait);

        Assert.True(worker.Finishing.IsCompleted);
        Assert.Null(_cache.TryGet(Item(0).Key));
        Assert.Equal(0, _queue.Snapshot().Pending + _queue.Snapshot().Working);
        Assert.Equal([(1, 7, 0, "span-s", "working")], _broadcaster.Sent);
    }

    private sealed class BlockingUpscaler : IPageUpscaler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<UpscaleResult> UpscaleAsync(byte[] page, string modelId, CancellationToken ct)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new UnreachableException();
        }
    }

    private sealed class DeletingUpscaler(UpscaleModelStore store) : IPageUpscaler
    {
        public async Task<UpscaleResult> UpscaleAsync(byte[] page, string modelId, CancellationToken ct)
        {
            await store.DeleteAsync(UpscaleModelId.SpanS);
            return UpscaleResult.Ready(new UpscaledImage([1, 2, 3, 4], "webp", 2, 2, 1));
        }
    }

    /// <summary>
    /// Two stages that report as they go, numbered by call. A page's finish waits until the test
    /// calls <see cref="Release"/> for it.
    /// </summary>
    private sealed class StagedUpscaler : IPageUpscaler
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _started = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _finishing = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _released = new();
        private int _inFinish;
        private int _maxFinishing;

        public int Calls;

        public int? ThrowWhileFinishing { get; init; }

        public TimeSpan FinishDelay { get; init; }

        public int MaxFinishing => Volatile.Read(ref _maxFinishing);

        public TaskCompletionSource Started(int call) => Signal(_started, call);

        public TaskCompletionSource Finishing(int call) => Signal(_finishing, call);

        public void Release(int call) => Signal(_released, call).TrySetResult();

        private static TaskCompletionSource Signal(ConcurrentDictionary<int, TaskCompletionSource> signals, int call) =>
            signals.GetOrAdd(call, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public Task<UpscaleResult> UpscaleAsync(byte[] page, string modelId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<PendingUpscale> StartAsync(byte[] page, string modelId, CancellationToken ct)
        {
            var call = Interlocked.Increment(ref Calls) - 1;
            Started(call).TrySetResult();
            return Task.FromResult(new PendingUpscale(async finish =>
            {
                var now = Interlocked.Increment(ref _inFinish);
                InterlockedMax(ref _maxFinishing, now);
                try
                {
                    Finishing(call).TrySetResult();
                    await Signal(_released, call).Task.WaitAsync(finish);
                    await Task.Delay(FinishDelay, finish);
                    if (ThrowWhileFinishing == call)
                    {
                        throw new InvalidOperationException("boom");
                    }

                    return UpscaleResult.Ready(new UpscaledImage([1, 2, 3, (byte)call], "webp", 2, 2, 1));
                }
                finally
                {
                    Interlocked.Decrement(ref _inFinish);
                }
            }));
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
            {
            }
        }
    }
}
