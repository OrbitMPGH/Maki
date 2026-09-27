using System.Net;
using Maki.Api.Configuration;
using Maki.Api.Services;
using Maki.Core.Sources;

namespace Maki.Api.Tests;

[Collection(ConfigDirCollection.Name)]
public class SourceProbeServiceTests : IDisposable
{
    private readonly UpgradeWorld _world = new();

    public void Dispose() => _world.Dispose();

    private ISource Official => _world.Registry.GetRequired(UpgradeWorld.Official);

    private static SourceChapter Chapter => new(UpgradeWorld.Official, "official-series", "o1", "1", 1m, null, null, "en", null);

    private static int PageIndex(string url) => int.Parse(Path.GetFileNameWithoutExtension(new Uri(url).AbsolutePath));

    [Fact]
    public async Task Samples_pages_spread_across_the_whole_chapter_and_measures_them()
    {
        _world.OfficialPages = UpgradeWorld.UrlPages(20);
        _world.Http.Width = 1600;

        var result = await _world.Probes().ProbeAsync(Official, UpgradeWorld.Official, Chapter, 6, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(20, result.PageCount);
        Assert.Equal(1600, result.MedianWidth);
        Assert.Equal(6, result.SampledPages);
        Assert.True(result.SampleBytes > 0);
        var indexes = _world.Http.Requested.Select(PageIndex).Order().ToList();
        Assert.Equal([1, 5, 8, 11, 15, 18], indexes);
    }

    [Fact]
    public void Sample_indexes_take_every_page_of_a_short_chapter()
    {
        Assert.Equal([0, 1, 2], SourceProbeService.SampleIndexes(3, 6));
        Assert.Equal(6, SourceProbeService.SampleIndexes(200, 6).Distinct().Count());
        Assert.True(SourceProbeService.SampleIndexes(200, 6)[^1] > 150);
    }

    [Fact]
    public async Task Null_when_the_source_lists_no_pages()
    {
        _world.OfficialPages = UpgradeWorld.UrlPages(0);

        Assert.Null(await _world.Probes().ProbeAsync(Official, UpgradeWorld.Official, Chapter, 6, CancellationToken.None));
        Assert.Empty(_world.Http.Requested);
    }

    [Fact]
    public async Task Null_when_every_sampled_download_fails_and_the_scratch_folder_is_gone()
    {
        _world.OfficialPages = UpgradeWorld.UrlPages(10);
        _world.Http.Status = HttpStatusCode.InternalServerError;

        Assert.Null(await _world.Probes().ProbeAsync(Official, UpgradeWorld.Official, Chapter, 6, CancellationToken.None));
        Assert.Equal(6, _world.Http.Requested.Count);
        AssertNoScratchLeft();
    }

    [Fact]
    public async Task Removes_its_scratch_folder_after_a_successful_probe()
    {
        _world.OfficialPages = UpgradeWorld.UrlPages(8);

        Assert.NotNull(await _world.Probes().ProbeAsync(Official, UpgradeWorld.Official, Chapter, 6, CancellationToken.None));
        AssertNoScratchLeft();
    }

    [Fact]
    public async Task A_source_in_cooldown_is_not_probed()
    {
        _world.Queue.EnterRateLimitCooldown(UpgradeWorld.Official, TimeSpan.FromMinutes(5));

        Assert.Null(await _world.Probes().ProbeAsync(Official, UpgradeWorld.Official, Chapter, 6, CancellationToken.None));
        Assert.Empty(_world.Http.Requested);
    }

    [Fact]
    public async Task A_429_enters_the_shared_cooldown()
    {
        _world.OfficialPages = UpgradeWorld.UrlPages(10);
        _world.Http.Status = HttpStatusCode.TooManyRequests;

        Assert.Null(await _world.Probes().ProbeAsync(Official, UpgradeWorld.Official, Chapter, 6, CancellationToken.None));
        Assert.True(_world.Queue.CooldownRemaining(UpgradeWorld.Official) > TimeSpan.Zero);
        Assert.Single(_world.Http.Requested);
        AssertNoScratchLeft();
    }

    private static void AssertNoScratchLeft()
    {
        var probeRoot = Path.Combine(new AppPaths().DownloadCacheDir, "probe");
        Assert.True(!Directory.Exists(probeRoot) || !Directory.EnumerateFileSystemEntries(probeRoot).Any());
    }
}
