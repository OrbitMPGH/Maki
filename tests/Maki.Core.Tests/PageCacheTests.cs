using Maki.Core.Io;
using Xunit;

namespace Maki.Core.Tests;

/// <summary>
/// The drop is an optimisation on a hot path that already did its real work, so the only thing that
/// must hold is that it never becomes a reason for that work to fail. It is a no-op off Linux,
/// which is where these run, so what is pinned here is the guard rather than the syscall.
/// </summary>
public class PageCacheTests
{
    [Fact]
    public void MissingFileIsIgnored() =>
        PageCache.DropAfterScan(Path.Combine(Path.GetTempPath(), $"maki-absent-{Guid.NewGuid():N}.bin"));

    [Fact]
    public void EmptyPathIsIgnored()
    {
        PageCache.DropAfterScan("");
        PageCache.DropAfterScan("   ");
    }

    [Fact]
    public void OpenFileIsNotDisturbed()
    {
        var path = Path.Combine(Path.GetTempPath(), $"maki-page-cache-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(path, new byte[4096]);
            using var held = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            PageCache.DropAfterScan(path);
            Assert.Equal(4096, held.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
