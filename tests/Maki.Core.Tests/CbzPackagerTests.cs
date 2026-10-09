using System.IO.Compression;
using Maki.Core.Download;

namespace Maki.Core.Tests;

public class CbzPackagerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("maki-packager-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private List<string> Pages(int count)
    {
        var files = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var file = Path.Combine(_dir, $"src{i}.jpg");
            File.WriteAllBytes(file, [1]);
            files.Add(file);
        }

        return files;
    }

    private static List<string> PageEntries(string cbz)
    {
        using var archive = ZipFile.OpenRead(cbz);
        return archive.Entries.Select(e => e.FullName).Where(n => n != "ComicInfo.xml").ToList();
    }

    [Fact]
    public void Names_pages_with_three_digits_by_default()
    {
        var target = Path.Combine(_dir, "out.cbz");
        CbzPackager.Package(Pages(3), "<ComicInfo/>", target);
        Assert.Equal(["001.jpg", "002.jpg", "003.jpg"], PageEntries(target));
    }

    [Fact]
    public void Pads_wider_so_page_names_still_sort_past_999()
    {
        var target = Path.Combine(_dir, "out.cbz");
        CbzPackager.Package(Pages(1001), "<ComicInfo/>", target);

        var names = PageEntries(target);
        Assert.Equal("0001.jpg", names[0]);
        Assert.Equal("1001.jpg", names[^1]);
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
    }
}
