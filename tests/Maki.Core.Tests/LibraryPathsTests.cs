using Maki.Core.Paths;

namespace Maki.Core.Tests;

public class LibraryPathsTests
{
    [Fact]
    public void Resolve_joins_root_and_relative_path()
    {
        var root = Directory.CreateTempSubdirectory("maki-library-paths-").FullName;

        var result = LibraryPaths.Resolve(root, "My Series (2017)");

        Assert.Equal(Path.Combine(root, "My Series (2017)"), result);
    }

    // Regression: a root folder path saved with the wrong separator style (forward slashes typed
    // into a Windows install) used to reach the UI as e.g. "/data/library/manga\My Series (2017)"
    // because SeriesDto.FromEntity joined it with a bare Path.Combine, which only supplies the
    // platform separator between its arguments and never touches what's already inside them.
    // LibraryPaths.Resolve runs the join through Path.GetFullPath, which normalizes every separator
    // in the result to the platform's own; this is what the display path is built from now.
    [Fact]
    public void Resolve_normalizes_a_root_path_saved_with_the_wrong_separator_style()
    {
        if (!OperatingSystem.IsWindows())
        {
            // The bug only exists on Windows: Path.Combine there always inserts '\', so a root
            // path stored with '/' produces a mixed result. On Linux the platform separator is
            // '/' already, so a root path typed with '/' was never mixed to begin with.
            return;
        }

        var result = LibraryPaths.Resolve("C:/library/manga", "My Series (2017)");

        Assert.Equal(@"C:\library\manga\My Series (2017)", result);
        Assert.DoesNotContain('/', result!);
    }

    [Fact]
    public void Resolve_rejects_a_relative_path_that_escapes_the_root()
    {
        var root = Directory.CreateTempSubdirectory("maki-library-paths-").FullName;

        Assert.Null(LibraryPaths.Resolve(root, "../elsewhere"));
    }

    // Regression: TrimEndingDirectorySeparator is a no-op on a drive root or the filesystem root,
    // so `root` already ends with a separator there; appending another before the StartsWith check
    // used to make every relative path fail containment (e.g. "D:\\" + "\\My Series" never matches
    // "D:\My Series").
    [Fact]
    public void Resolve_handles_a_drive_or_filesystem_root()
    {
        var root = OperatingSystem.IsWindows() ? "D:\\" : "/";

        var result = LibraryPaths.Resolve(root, "My Series (2017)");

        Assert.Equal(Path.Combine(root, "My Series (2017)"), result);
    }

    [Fact]
    public void TopFolder_reads_the_first_segment()
    {
        Assert.Equal("My Series", LibraryPaths.TopFolder(Path.Combine("My Series", "ch1.cbz")));
        Assert.Null(LibraryPaths.TopFolder("ch1.cbz"));
    }
}
