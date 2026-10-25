using Maki.Api.Configuration;
using Maki.Api.Services;
using Maki.Api.Services.Upscaling;
using Maki.Core.Configuration;
using Maki.Core.Images.Upscaling;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests.Upscaling;

public sealed class UpscaleWorkerTests : IDisposable
{
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
        UpscaleTestKit.WriteCbz(_cbz, pages: 3);
    }

    public void Dispose() => UpscaleTestKit.TryDelete(_configDir);

    private UpscaleWorker NewWorker(IPageUpscaler upscaler) => new(
        _queue, _cache, upscaler, _store, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance), _paths, _settings,
        _broadcaster, NullLogger<UpscaleWorker>.Instance);

    private ReaderService.PageSlice Slice() =>
        new(7, 3, _cbz, new FileInfo(_cbz).Length, ["001.png", "002.png", "003.png"], 0, 3, 99);

    private UpscaleRequestItem Item(int page) =>
        new(new UpscaleWorkKey(3, Slice().ArchiveVersion, page, "span-s"), new UpscaleTarget(7, page), Slice());

    [Fact]
    public async Task A_job_runs_the_model_writes_the_cache_and_tells_every_waiting_reader()
    {
        using var upscaler = new OnnxPageUpscaler(_catalog, _store, _settings, NullLogger<OnnxPageUpscaler>.Instance);
        _queue.Replace(1, 7, "span-s", [Item(1)]);
        _queue.Replace(2, 7, "span-s", [Item(1)]);

        Assert.True(await NewWorker(upscaler).RunOnceAsync(default));

        var cached = _cache.TryGet(Item(1).Key);
        Assert.NotNull(cached);
        Assert.EndsWith(".span-s.webp", cached);
        Assert.Equal(
            [(1, 7, 1, "span-s", "ready"), (2, 7, 1, "span-s", "ready")],
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
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (_broadcaster.Sent.Count < 2 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
        }
        finally
        {
            await worker.StopAsync(default);
        }

        Assert.Equal([(1, 7, 0, "span-s", "failed"), (1, 7, 2, "span-s", "ready")], _broadcaster.Sent);
        Assert.Equal(UpscaleOutcome.Failed, _queue.RecentOutcome(Item(0).Key));
        Assert.NotNull(_cache.TryGet(Item(2).Key));
    }

    [Fact]
    public async Task With_the_tier_off_nothing_runs_and_pending_work_is_dropped()
    {
        _settings.Set(SettingKeys.UpscalingEnabled, "false");
        var upscaler = new FakeUpscaler();
        _queue.Replace(1, 7, "span-s", [Item(0)]);

        Assert.False(await NewWorker(upscaler).RunOnceAsync(default));

        Assert.Equal(0, upscaler.Calls);
        Assert.Equal(0, _queue.Snapshot().Pending);
    }

    [Fact]
    public async Task A_page_for_a_model_the_admin_unticked_fails_without_running()
    {
        _settings.Set(SettingKeys.UpscalingModels, "waifu2x-art");
        var upscaler = new FakeUpscaler();
        _queue.Replace(1, 7, "span-s", [Item(0)]);

        Assert.True(await NewWorker(upscaler).RunOnceAsync(default));

        Assert.Equal(0, upscaler.Calls);
        Assert.Equal([(1, 7, 0, "span-s", "failed")], _broadcaster.Sent);
        Assert.Null(_queue.RecentOutcome(Item(0).Key));
    }

    [Fact]
    public async Task A_model_deleted_while_its_page_ran_leaves_nothing_in_the_cache()
    {
        var upscaler = new DeletingUpscaler(_store);
        _queue.Replace(1, 7, "span-s", [Item(0)]);

        Assert.True(await NewWorker(upscaler).RunOnceAsync(default));

        Assert.Null(_cache.TryGet(Item(0).Key));
        Assert.Equal([(1, 7, 0, "span-s", "failed")], _broadcaster.Sent);
        Assert.Null(_queue.RecentOutcome(Item(0).Key));
    }

    [Fact]
    public async Task A_page_that_could_not_be_cached_is_not_remembered_as_failed()
    {
        Directory.CreateDirectory(_cache.Root);
        await File.WriteAllBytesAsync(Path.Combine(_cache.Root, "3"), [1]);
        _queue.Replace(1, 7, "span-s", [Item(0)]);

        Assert.True(await NewWorker(new FakeUpscaler()).RunOnceAsync(default));

        Assert.Equal([(1, 7, 0, "span-s", "failed")], _broadcaster.Sent);
        Assert.Null(_queue.RecentOutcome(Item(0).Key));
    }

    private sealed class DeletingUpscaler(UpscaleModelStore store) : IPageUpscaler
    {
        public async Task<UpscaleResult> UpscaleAsync(byte[] page, string modelId, CancellationToken ct)
        {
            await store.DeleteAsync(UpscaleModelId.SpanS);
            return UpscaleResult.Ready(new UpscaledImage([1, 2, 3, 4], "webp", 2, 2, 1));
        }
    }
}
