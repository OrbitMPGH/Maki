using Maki.Api.Configuration;

namespace Maki.Api.Tests;

public class SharedDumpPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maki-shared-dump-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Without_a_shared_folder_the_dump_lives_in_the_config_dir()
    {
        var config = Path.Combine(_root, "config");
        var paths = new AppPaths(config);

        Assert.False(paths.SharesMangaBakaDump);
        Assert.Equal(Path.Combine(config, "mangabaka.db"), paths.MangaBakaDbPath);
    }

    [Fact]
    public void A_shared_folder_moves_only_the_dump()
    {
        var config = Path.Combine(_root, "config");
        var shared = Path.Combine(_root, "simulated");
        var paths = new AppPaths(config, shared);

        Assert.True(paths.SharesMangaBakaDump);
        Assert.Equal(Path.Combine(shared, "mangabaka.db"), paths.MangaBakaDbPath);
        Assert.Equal(Path.Combine(config, "maki.db"), paths.DatabasePath);
        Assert.Equal(Path.Combine(config, "embeddings.db"), paths.EmbeddingsDbPath);
        Assert.Equal(Path.Combine(config, "ease-table.db"), paths.EaseDbPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_blank_shared_folder_is_ignored(string shared) =>
        Assert.False(new AppPaths(Path.Combine(_root, "config"), shared).SharesMangaBakaDump);

    [Fact]
    public void Pointing_the_shared_folder_at_the_config_dir_still_owns_the_dump()
    {
        var config = Path.Combine(_root, "config");
        var paths = new AppPaths(config, config + Path.DirectorySeparatorChar);

        Assert.False(paths.SharesMangaBakaDump);
    }
}
