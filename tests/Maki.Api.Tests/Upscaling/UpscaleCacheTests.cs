using Maki.Api.Configuration;
using Maki.Api.Jobs;
using Maki.Api.Services;
using Maki.Api.Services.Upscaling;
using Maki.Core.Entities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests.Upscaling;

public sealed class UpscaleCacheTests : IDisposable
{
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "maki-upscale-cache-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly FakeAppSettings _settings = new();

    public UpscaleCacheTests() => _paths = new AppPaths(_configDir);

    public void Dispose() => UpscaleTestKit.TryDelete(_configDir);

    private UpscaleCache NewCache(long? capBytes = null) =>
        new(_paths, _settings, NullLogger<UpscaleCache>.Instance) { CapBytesOverride = capBytes };

    private static UpscaleWorkKey Key(int page, int fileId = 1, string version = "1000-5", string model = "span-s") =>
        new(fileId, version, page, model);

    [Fact]
    public async Task Writes_land_under_their_own_root_with_the_planned_name()
    {
        var cache = NewCache();

        var path = await cache.WriteAsync(Key(3, fileId: 12), [1, 2, 3], "webp", default);

        Assert.Equal(Path.Combine(_configDir, "cache", "upscale", "12", "1000-5-3.span-s.webp"), path);
        Assert.Equal(path, cache.TryGet(Key(3, fileId: 12)));
        Assert.Equal((3L, 1), cache.Stats());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public async Task Eviction_takes_the_least_recently_read_page_first()
    {
        var cache = NewCache(capBytes: 300);
        await cache.WriteAsync(Key(0), new byte[100], "webp", default);
        await cache.WriteAsync(Key(1), new byte[100], "webp", default);
        await cache.WriteAsync(Key(2), new byte[100], "webp", default);
        await Task.Delay(20);
        Assert.NotNull(cache.TryGet(Key(0)));

        await cache.WriteAsync(Key(3), new byte[100], "webp", default);

        Assert.NotNull(cache.TryGet(Key(0)));
        Assert.Null(cache.TryGet(Key(1)));
        Assert.NotNull(cache.TryGet(Key(2)));
        Assert.NotNull(cache.TryGet(Key(3)));
        Assert.Equal((300L, 3), cache.Stats());
    }

    [Fact]
    public async Task The_cap_holds_after_concurrent_writes()
    {
        var cache = NewCache(capBytes: 1000);

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i =>
            Task.Run(() => cache.WriteAsync(Key(i, fileId: 1 + i % 3), new byte[100], "webp", default))));

        var (bytes, files) = cache.Stats();
        Assert.True(bytes <= 1000, $"{bytes} bytes over the cap");
        var onDisk = Directory.GetFiles(cache.Root, "*", SearchOption.AllDirectories);
        Assert.Equal(files, onDisk.Length);
        Assert.Equal(bytes, onDisk.Sum(f => new FileInfo(f).Length));
    }

    [Fact]
    public async Task A_restart_finds_what_is_on_disk()
    {
        await NewCache().WriteAsync(Key(0), new byte[50], "jpg", default);

        var fresh = NewCache();
        await fresh.EnsureSeededAsync();

        Assert.Equal((50L, 1), fresh.Stats());
    }

    [Fact]
    public async Task Removing_a_model_and_clearing_delete_only_what_they_name()
    {
        var cache = NewCache();
        await cache.WriteAsync(Key(0), [1], "webp", default);
        await cache.WriteAsync(Key(0, model: "realesrgan-av3"), [1], "webp", default);

        await cache.RemoveModelAsync("span-s", default);
        Assert.Null(cache.TryGet(Key(0)));
        Assert.NotNull(cache.TryGet(Key(0, model: "realesrgan-av3")));

        await cache.ClearAsync(default);
        Assert.Equal((0L, 0), cache.Stats());
        Assert.Empty(Directory.GetFiles(cache.Root, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Clearing_and_pruning_leave_a_page_still_being_written()
    {
        var cache = NewCache();
        await cache.WriteAsync(Key(0, fileId: 1), [1], "webp", default);
        await cache.WriteAsync(Key(0, fileId: 2), [1], "webp", default);
        var writing = Path.Combine(cache.Root, "1", "fresh.tmp");
        var orphanWriting = Path.Combine(cache.Root, "2", "fresh.tmp");
        var abandoned = Path.Combine(cache.Root, "1", "old.tmp");
        foreach (var tmp in (string[])[writing, orphanWriting, abandoned])
        {
            await File.WriteAllBytesAsync(tmp, [1]);
        }

        File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddHours(-2));

        await cache.ClearAsync(default);
        Assert.True(File.Exists(writing));
        Assert.False(File.Exists(abandoned));

        await cache.PruneAsync(new Dictionary<int, long>(), default);
        Assert.True(File.Exists(writing));
        Assert.True(File.Exists(orphanWriting));
        Assert.Equal((0L, 0), cache.Stats());
    }

    [Fact]
    public async Task Housekeeping_drops_stale_versions_and_orphan_folders_and_leaves_the_reader_cache_alone()
    {
        using var testDb = new TestDb();
        int liveId;
        using (var db = testDb.NewContext())
        {
            var root = new RootFolder { Path = Path.Combine(_configDir, "library") };
            db.RootFolders.Add(root);
            db.SaveChanges();
            var series = new Series { Title = "S", SortTitle = "s", FolderName = "S", RootFolderId = root.Id };
            db.Series.Add(series);
            db.SaveChanges();
            var file = new ChapterFile { SeriesId = series.Id, RelativePath = "S/c1.cbz", Size = 500, SourceName = "test", DateAdded = DateTime.UtcNow };
            db.ChapterFiles.Add(file);
            db.SaveChanges();
            liveId = file.Id;
        }

        var cache = NewCache();
        await cache.WriteAsync(Key(0, fileId: liveId, version: "500-200"), [1], "webp", default);
        await cache.WriteAsync(Key(1, fileId: liveId, version: "500-200"), [1], "jpg", default);
        await cache.WriteAsync(Key(0, fileId: liveId, version: "500-100"), [1], "webp", default);
        await cache.WriteAsync(Key(0, fileId: liveId, version: "400-300"), [1], "webp", default);
        await cache.WriteAsync(Key(0, fileId: liveId + 50), [1], "webp", default);

        var readerDir = Path.Combine(_paths.ReaderCacheDir, liveId.ToString());
        Directory.CreateDirectory(readerDir);
        var readerFile = Path.Combine(readerDir, "500-200-0.jpg");
        await File.WriteAllBytesAsync(readerFile, [1]);
        var foreign = Path.Combine(cache.Root, liveId.ToString(), "notes.txt");
        await File.WriteAllBytesAsync(foreign, [1]);

        using (var db = testDb.NewContext())
        {
            var job = new HousekeepingJob(db, _paths,
                new UpgradeTrashService(db, _settings, NullLogger<UpgradeTrashService>.Instance),
                cache, NullLogger<HousekeepingJob>.Instance);
            await job.Execute(new TestJobContext());
        }

        var left = Directory.GetFiles(cache.Root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(cache.Root, f).Replace('\\', '/')).Order().ToArray();
        Assert.Equal([$"{liveId}/500-200-0.span-s.webp", $"{liveId}/500-200-1.span-s.jpg"], left);
        Assert.False(Directory.Exists(Path.Combine(cache.Root, (liveId + 50).ToString())));
        Assert.Equal((2L, 2), cache.Stats());
        Assert.True(File.Exists(readerFile));
    }
}
