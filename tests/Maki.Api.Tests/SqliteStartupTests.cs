using Maki.Api.Configuration;
using Maki.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Maki.Api.Tests;

public class SqliteStartupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "maki-sqlitestartup-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Prepare_creates_the_database_before_the_keeper_and_sweeps_a_directory_once_per_process()
    {
        var paths = new AppPaths(_dir);
        var stale = Path.Combine(_dir, "embeddings.db-shm");
        File.WriteAllBytes(stale, new byte[32768]);

        SqliteStartup.Prepare(paths, NullLogger.Instance);

        Assert.True(File.Exists(paths.DatabasePath));
        Assert.Equal(OperatingSystem.IsLinux(), SqliteKeeper.For(paths.DatabasePath).IsOpen);
        // Linux sweeps; elsewhere SQLite never leaves one that kills the process, so it is left alone.
        Assert.Equal(!OperatingSystem.IsLinux(), File.Exists(stale));

        File.WriteAllBytes(stale, new byte[32768]);
        SqliteStartup.Prepare(paths, NullLogger.Instance);

        Assert.True(File.Exists(stale));

        var keeper = SqliteKeeper.For(paths.DatabasePath);
        SqliteStartup.Release(paths);
        Assert.False(keeper.IsOpen);
        SqliteKeeper.For(paths.DatabasePath).Attach();
        Assert.False(SqliteKeeper.For(paths.DatabasePath).IsOpen);
    }
}
