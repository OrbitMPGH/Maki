using System.IO.Compression;
using Maki.Core.Reading;

namespace Maki.Core.Tests;

public class CbzReaderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("maki-cbz-reader-").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private string WriteZip(string name, params string[] entries)
    {
        var path = Path.Combine(_root, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
            writer.Write("page");
        }

        return path;
    }

    [Fact]
    public void Opens_a_readable_entry()
    {
        var path = WriteZip("ok.cbz", "001.jpg");

        using var stream = CbzReader.OpenPage(path, "001.jpg");

        Assert.NotNull(stream);
    }

    [Fact]
    public void Returns_null_for_a_missing_entry_rather_than_throwing()
    {
        var path = WriteZip("ok.cbz", "001.jpg");

        Assert.Null(CbzReader.OpenPage(path, "does-not-exist.jpg"));
    }

    // Regression: a corrupt local file header used to throw InvalidDataException out of
    // ZipArchiveEntry.Open() itself, which OpenPage let escape, so ReaderController/OpdsController
    // then 500'd instead of the 404 every other "page not readable" case returns.
    [Fact]
    public void A_corrupt_entry_returns_null_instead_of_throwing()
    {
        var path = WriteZip("corrupt.cbz", "001.jpg");

        // The local file header sits at the very start of the archive; the central directory (at
        // the end) still names the entry, so ZipFile.OpenRead succeeds but entry.Open() fails
        // reading the local header.
        var bytes = File.ReadAllBytes(path);
        for (var i = 0; i < 4 && i < bytes.Length; i++)
        {
            bytes[i] = 0xFF;
        }

        File.WriteAllBytes(path, bytes);

        Assert.Null(CbzReader.OpenPage(path, "001.jpg"));
    }

    // Regression: a zip whose central directory itself cannot be read used to throw straight out
    // of ZipFile.OpenRead, before OpenPage's own try/catch had a chance to run.
    [Fact]
    public void An_unreadable_archive_returns_null_instead_of_throwing()
    {
        var path = Path.Combine(_root, "not-a-zip.cbz");
        File.WriteAllText(path, "not a zip at all");

        Assert.Null(CbzReader.OpenPage(path, "001.jpg"));
    }
}
