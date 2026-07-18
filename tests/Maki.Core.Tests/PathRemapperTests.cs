using Maki.Core.Paths;

namespace Maki.Core.Tests;

public class PathRemapperTests
{
    [Theory]
    // no mapping configured → unchanged
    [InlineData(@"C:\Manga\Series A", null, null, @"C:\Manga\Series A")]
    [InlineData(@"C:\Manga\Series A", @"C:\Manga", null, @"C:\Manga\Series A")]
    [InlineData(@"C:\Manga\Series A", "", "/manga", @"C:\Manga\Series A")]
    // windows → unix (target in Docker), separators converted
    [InlineData(@"C:\Manga\Series A", @"C:\Manga", "/manga", "/manga/Series A")]
    [InlineData(@"C:\Manga\Series A\Sub", @"C:\Manga", "/manga", "/manga/Series A/Sub")]
    // prefix match is case-insensitive and tolerates trailing separators on both sides
    [InlineData(@"c:\manga\Series A", @"C:\Manga\", "/manga/", "/manga/Series A")]
    // path equal to the prefix maps to the bare target
    [InlineData(@"C:\Manga", @"C:\Manga", "/manga", "/manga")]
    // prefix must end on a path boundary
    [InlineData(@"C:\MangaExtra\Series A", @"C:\Manga", "/manga", @"C:\MangaExtra\Series A")]
    // non-matching prefix → unchanged
    [InlineData(@"D:\Other\Series A", @"C:\Manga", "/manga", @"D:\Other\Series A")]
    // separator style of the configured prefix doesn't have to match the actual path
    [InlineData(@"C:\Manga\Series A", "C:/Manga", "/manga", "/manga/Series A")]
    [InlineData("C:/Manga/Series A", @"C:\Manga", "/manga", "/manga/Series A")]
    // unix → windows
    [InlineData("/library/Series A", "/library", @"\\nas\manga", @"\\nas\manga\Series A")]
    // UNC → unix
    [InlineData(@"\\nas\manga\Series A", @"\\nas\manga", "/manga", "/manga/Series A")]
    // qBittorrent-in-Docker download path → Maki's Windows mount
    [InlineData("/downloads/Series v01.cbz", "/downloads", @"Z:\downloads", @"Z:\downloads\Series v01.cbz")]
    [InlineData("/downloads", "/downloads", @"Z:\downloads", @"Z:\downloads")]
    // both sides unix, different mount roots
    [InlineData("/data/torrents/x.cbz", "/data/torrents", "/mnt/user/downloads", "/mnt/user/downloads/x.cbz")]
    public void Map(string path, string? mapFrom, string? mapTo, string expected) =>
        Assert.Equal(expected, PathRemapper.Map(path, mapFrom, mapTo));
}
