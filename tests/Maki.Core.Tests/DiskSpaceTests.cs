using Maki.Core.Storage;

namespace Maki.Core.Tests;

public class DiskSpaceTests
{
    private static readonly string[] DockerMounts = ["/", "/config", "/manga", "/manga/nas", "/proc"];

    [Theory]
    [InlineData("/manga", "/manga")]
    [InlineData("/manga/Berserk", "/manga")]
    [InlineData("/manga/nas/Berserk", "/manga/nas")]
    [InlineData("/manga2/Berserk", "/")]
    [InlineData("/app/data", "/")]
    public void A_linux_folder_resolves_to_its_own_mount_rather_than_the_root(string path, string expected)
    {
        Assert.Equal(expected, DiskSpace.LongestMount(path, DockerMounts, StringComparison.Ordinal));
    }

    [Fact]
    public void A_mount_name_with_a_trailing_separator_still_matches()
    {
        Assert.Equal(@"D:\", DiskSpace.LongestMount(@"D:\Manga", [@"C:\", @"D:\"], StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(@"d:\Manga", @"D:\")]
    [InlineData(@"E:\Manga", null)]
    public void Windows_drive_letters_match_case_insensitively(string path, string? expected)
    {
        Assert.Equal(expected, DiskSpace.LongestMount(path, [@"C:\", @"D:\"], StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void No_matching_mount_is_null()
    {
        Assert.Null(DiskSpace.LongestMount("/manga", ["/config"], StringComparison.Ordinal));
    }

    [Fact]
    public void A_real_folder_reports_free_space()
    {
        Assert.True(DiskSpace.AvailableFor(Path.GetTempPath()) > 0);
    }
}
