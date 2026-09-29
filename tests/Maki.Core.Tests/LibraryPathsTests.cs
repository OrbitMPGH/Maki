using Maki.Core.Paths;

namespace Maki.Core.Tests;

public class LibraryPathsTests
{
    [Fact]
    public void IsSameDirectory_tells_a_case_only_spelling_from_a_second_folder()
    {
        var root = Directory.CreateTempSubdirectory("maki-same-dir-").FullName;
        try
        {
            var lower = Path.Combine(root, "chainsaw man");
            var upper = Path.Combine(root, "Chainsaw Man");
            Directory.CreateDirectory(lower);
            Directory.CreateDirectory(Path.Combine(root, "Other"));

            Assert.True(LibraryPaths.IsSameDirectory(lower, lower + Path.DirectorySeparatorChar));
            Assert.False(LibraryPaths.IsSameDirectory(lower, Path.Combine(root, "Other")));
            if (Directory.Exists(upper))
            {
                // Case-insensitive filesystem: both spellings are the one folder.
                Assert.True(LibraryPaths.IsSameDirectory(lower, upper));
            }
            else
            {
                Directory.CreateDirectory(upper);
                Assert.False(LibraryPaths.IsSameDirectory(lower, upper));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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

    [Theory]
    [InlineData("My Series\\ch1.cbz")]
    [InlineData("My Series/ch1.cbz")]
    public void TopFolder_reads_either_separator(string relativePath)
    {
        Assert.Equal("My Series", LibraryPaths.TopFolder(relativePath));
    }

    // Regression: a "." or ".." top segment is not a real folder name, but a caller treating it as
    // one (SeriesFolders.ForAsync feeding a rescan or a relink) would enumerate the whole root or its
    // parent. A bad ChapterFile.RelativePath used to produce exactly this from a raw request path
    // that resolved inside the root while still starting with "./" or "../".
    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public void TopFolder_treats_a_dot_segment_as_no_folder(string segment)
    {
        Assert.Null(LibraryPaths.TopFolder(Path.Combine(segment, "ch1.cbz")));
    }

    [Fact]
    public void ResolveNoLinks_refuses_a_path_through_a_linked_directory_inside_the_root()
    {
        var root = Directory.CreateTempSubdirectory("maki-nolinks-root-").FullName;
        var outside = Directory.CreateTempSubdirectory("maki-nolinks-outside-").FullName;
        var link = Path.Combine(root, "Series", "linked");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Series"));
            File.WriteAllText(Path.Combine(outside, "ch1.cbz"), "external");
            File.WriteAllText(Path.Combine(root, "Series", "ch2.cbz"), "inside");
            if (!TestLinks.TryLinkDirectory(link, outside))
            {
                return;
            }

            var through = Path.Combine("Series", "linked", "ch1.cbz");
            Assert.NotNull(LibraryPaths.Resolve(root, through));
            Assert.Null(LibraryPaths.ResolveNoLinks(root, through));
            Assert.Null(LibraryPaths.ResolveNoLinks(root, Path.Combine("Series", "linked")));
            Assert.Equal(Path.Combine(root, "Series", "ch2.cbz"),
                LibraryPaths.ResolveNoLinks(root, Path.Combine("Series", "ch2.cbz")));
            Assert.Equal(new[] { Path.Combine(root, "Series", "ch2.cbz") },
                LibraryPaths.EnumerateFilesNoLinks(Path.Combine(root, "Series")).ToList());
        }
        finally
        {
            TestLinks.UnlinkDirectory(link);
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void A_root_folder_reached_through_a_link_is_only_refused_when_asked()
    {
        var parent = Directory.CreateTempSubdirectory("maki-nolinks-parent-").FullName;
        var real = Directory.CreateTempSubdirectory("maki-nolinks-real-").FullName;
        var root = Path.Combine(parent, "library");
        try
        {
            Directory.CreateDirectory(Path.Combine(real, "Series"));
            if (!TestLinks.TryLinkDirectory(root, real))
            {
                return;
            }

            var path = LibraryPaths.ResolveNoLinks(root, "Series");
            Assert.NotNull(path);
            Assert.True(LibraryPaths.TraversesLink(root, path, includeRoot: true));
        }
        finally
        {
            TestLinks.UnlinkDirectory(root);
            Directory.Delete(parent, recursive: true);
            Directory.Delete(real, recursive: true);
        }
    }

    [Theory]
    [InlineData("...")]
    [InlineData("....")]
    [InlineData(". .")]
    [InlineData("... ")]
    [InlineData(".")]
    public void Resolve_never_returns_the_root_itself(string relative)
    {
        // Only Windows trims trailing dots and spaces; elsewhere "..." is an ordinary name.
        if (!OperatingSystem.IsWindows() && relative != ".")
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "maki-root");
        Assert.Null(LibraryPaths.Resolve(root, relative));
        Assert.Null(LibraryPaths.Resolve(root + Path.DirectorySeparatorChar, relative));
    }

    [Fact]
    public void ContainsLink_finds_a_nested_link_and_ResolveForDelete_allows_only_a_linked_leaf()
    {
        var root = Directory.CreateTempSubdirectory("maki-contains-root-").FullName;
        var outside = Directory.CreateTempSubdirectory("maki-contains-outside-").FullName;
        var link = Path.Combine(root, "Series", "Volume 1", "linked");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Series", "Volume 1"));
            File.WriteAllText(Path.Combine(outside, "ch1.cbz"), "external");
            Assert.False(LibraryPaths.ContainsLink(Path.Combine(root, "Series")));
            if (!TestLinks.TryLinkDirectory(link, outside))
            {
                return;
            }

            Assert.True(LibraryPaths.ContainsLink(Path.Combine(root, "Series")));
            Assert.Null(LibraryPaths.ResolveForDelete(root, Path.Combine("Series", "Volume 1", "linked", "ch1.cbz")));
            Assert.Equal(link, LibraryPaths.ResolveForDelete(root, Path.Combine("Series", "Volume 1", "linked")));
            Assert.Empty(LibraryPaths.EnumerateDirectoriesNoLinks(Path.Combine(root, "Series"))
                .Where(d => d.StartsWith(link, StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            TestLinks.UnlinkDirectory(link);
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }
}
