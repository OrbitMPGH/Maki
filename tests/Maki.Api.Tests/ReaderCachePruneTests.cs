using Maki.Api.Jobs;

namespace Maki.Api.Tests;

public sealed class ReaderCachePruneTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("maki-reader-prune-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Touch(params string[] names)
    {
        foreach (var name in names)
        {
            File.WriteAllBytes(Path.Combine(_dir, name), [1]);
        }
    }

    private string[] Left() => Directory.GetFiles(_dir).Select(Path.GetFileName).Order().ToArray()!;

    [Fact]
    public void Keeps_only_the_newest_stamp_of_the_current_size()
    {
        Touch("500-100-0.jpg", "500-100-1.full.jpg", "500-200-0.jpg", "500-200-0.full.jpg");

        HousekeepingJob.PruneReaderCacheDir(_dir, "500");

        Assert.Equal(["500-200-0.full.jpg", "500-200-0.jpg"], Left());
    }

    [Fact]
    public void Drops_other_sizes_and_the_old_unstamped_names()
    {
        Touch("400-300-0.jpg", "500-0.jpg", "500-7.full.jpg", "500-200-3.jpg");

        HousekeepingJob.PruneReaderCacheDir(_dir, "500");

        Assert.Equal(["500-200-3.jpg"], Left());
    }
}
