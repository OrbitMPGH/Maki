using Maki.Api.Services;

namespace Maki.Api.Tests;

public class MigrationErrorMarkerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("maki-migration-marker-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Records_the_exception_so_the_check_can_show_it()
    {
        MigrationErrorMarker.Write(_dir, new InvalidOperationException("database or disk is full\nsecond line"));

        var error = MigrationErrorMarker.Read(_dir, DateTime.UtcNow);

        Assert.Equal("InvalidOperationException: database or disk is full second line", error);
    }

    [Fact]
    public void Reports_nothing_when_no_migration_failed()
    {
        Assert.Null(MigrationErrorMarker.Read(_dir, DateTime.UtcNow));
    }

    [Fact]
    public void An_old_marker_expires_and_is_removed()
    {
        MigrationErrorMarker.Write(_dir, new InvalidOperationException("boom"));

        Assert.Null(MigrationErrorMarker.Read(_dir, DateTime.UtcNow + MigrationErrorMarker.Lifetime + TimeSpan.FromMinutes(1)));
        Assert.False(File.Exists(Path.Combine(_dir, MigrationErrorMarker.FileName)));
    }
}
