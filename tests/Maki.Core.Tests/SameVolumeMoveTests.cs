using Maki.Core.Storage;

namespace Maki.Core.Tests;

public sealed class SameVolumeMoveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"maki-move-{Guid.NewGuid():N}");

    public SameVolumeMoveTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Moves_and_refuses_to_replace_an_existing_target()
    {
        var a = Path.Combine(_dir, "a.cbz");
        var b = Path.Combine(_dir, "b.cbz");
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");

        Assert.Equal(MoveResult.TargetExists, SameVolumeMove.Move(a, b).Result);
        Assert.Equal("a", File.ReadAllText(a));
        Assert.Equal("b", File.ReadAllText(b));

        File.Delete(b);
        var outcome = SameVolumeMove.Move(a, b);
        Assert.Equal(MoveResult.Moved, outcome.Result);
        Assert.False(outcome.Racy && OperatingSystem.IsWindows());
        Assert.False(File.Exists(a));
        Assert.Equal("a", File.ReadAllText(b));
    }

    [Fact]
    public void A_missing_source_fails_without_touching_the_target()
    {
        var b = Path.Combine(_dir, "b.cbz");
        Assert.Equal(MoveResult.Failed, SameVolumeMove.Move(Path.Combine(_dir, "gone.cbz"), b).Result);
        Assert.False(File.Exists(b));
    }

    [Fact]
    public void Moves_a_path_longer_than_max_path()
    {
        var deep = Path.Combine(_dir, new string('d', 120), new string('e', 120));
        Directory.CreateDirectory(deep);
        var a = Path.Combine(deep, new string('a', 30) + ".cbz");
        var b = Path.Combine(deep, new string('b', 30) + ".cbz");
        File.WriteAllText(a, "a");
        Assert.True(b.Length > 260);

        Assert.Equal(MoveResult.Moved, SameVolumeMove.Move(a, b).Result);
        Assert.Equal("a", File.ReadAllText(b));
    }

    [Fact]
    public void Extended_prefixes_only_long_paths()
    {
        Assert.Equal(@"C:\x.cbz", SameVolumeMove.Extended(@"C:\x.cbz"));
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var local = @"C:\" + new string('a', 250);
        Assert.Equal(@"\\?\" + local, SameVolumeMove.Extended(local));
        var unc = @"\\server\share\" + new string('a', 250);
        Assert.Equal(@"\\?\UNC\server\share\" + new string('a', 250), SameVolumeMove.Extended(unc));
    }

    [Fact]
    public void Landed_means_source_gone_and_target_present()
    {
        var a = Path.Combine(_dir, "a.cbz");
        var b = Path.Combine(_dir, "b.cbz");
        File.WriteAllText(b, "b");
        Assert.True(SameVolumeMove.Landed(a, b));
        File.WriteAllText(a, "a");
        Assert.False(SameVolumeMove.Landed(a, b));
    }
}
