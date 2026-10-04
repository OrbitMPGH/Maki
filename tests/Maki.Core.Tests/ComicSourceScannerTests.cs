using System.IO.Compression;
using System.Text;
using Maki.Core.Import;
using Maki.Core.Reading;
using SharpCompress.Common;
using SharpCompress.Writers;

namespace Maki.Core.Tests;

/// <summary>
/// What a completed download or an import folder actually holds. Every shape here came off a real
/// instance: five of that library's thirty-seven grabs failed to import, and not one of them was
/// a CBZ.
/// </summary>
public class ComicSourceScannerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "maki-sources-" + Guid.NewGuid().ToString("N")[..8]);

    public ComicSourceScannerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    private string WriteZip(string relativePath, params string[] entries)
    {
        var path = At(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
            writer.Write("page");
        }

        return path;
    }

    /// <summary>
    /// A tar stands in for the RAR sets in the report: SharpCompress cannot write RAR, and both
    /// formats reach the converter through the same <c>ArchiveFactory</c> autodetect and the same
    /// entry stream. Only the container differs.
    /// </summary>
    private string WriteTar(string relativePath, params (string Name, byte[] Bytes)[] entries)
    {
        var path = At(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var stream = File.Create(path))
        using (var writer = WriterFactory.OpenWriter(
                   stream, ArchiveType.Tar, new WriterOptions(CompressionType.None)))
        {
            foreach (var (name, bytes) in entries)
            {
                writer.Write(name, new MemoryStream(bytes), DateTime.UtcNow);
            }
        }

        return path;
    }

    private static byte[] Page(string text) => Encoding.UTF8.GetBytes(text);

    private string WriteLooseFiles(string folder, params string[] names)
    {
        var dir = At(folder);
        Directory.CreateDirectory(dir);
        foreach (var name in names)
        {
            File.WriteAllText(Path.Combine(dir, name), "page");
        }

        return dir;
    }

    [Fact]
    public void A_cbz_is_left_alone()
    {
        WriteZip("My Series v01.cbz", "001.jpg", "002.jpg");

        var source = Assert.Single(ComicSourceScanner.Scan(_root));

        Assert.Equal("My Series v01.cbz", source.Name);
        Assert.Equal(ComicSourceKind.Cbz, source.Kind);
        Assert.Equal(2, source.Pages.Count);
    }

    // A CBZ already is a zip, so this one only ever needed the extension check to accept it.
    [Fact]
    public void A_zip_of_pages_is_a_comic_under_a_cbz_name()
    {
        WriteZip("Soratobi Tamashii.zip", "001.jpg", "002.jpg");

        var source = Assert.Single(ComicSourceScanner.Scan(_root));

        Assert.Equal("Soratobi Tamashii.cbz", source.Name);
        Assert.Equal(ComicSourceKind.Zip, source.Kind);
        Assert.True(source.IsReadyToPlace);
    }

    [Fact]
    public void A_zip_is_placed_without_being_rebuilt()
    {
        var zip = WriteZip("Soratobi Tamashii.zip", "001.jpg");
        var source = Assert.Single(ComicSourceScanner.Scan(_root));
        var target = At("out", source.Name);

        ComicSourceConverter.Materialize(source, target, useHardlinks: false);

        Assert.Equal(new FileInfo(zip).Length, new FileInfo(target).Length);
        Assert.Equal(["001.jpg"], CbzReader.PageNames(target));
    }

    [Fact]
    public void A_non_zip_archive_is_repacked_into_a_readable_cbz()
    {
        WriteTar(
            "Narutaru_vol.03.cbt",
            ("Narutaru - c018 - p001.jpg", Page("first")),
            ("Narutaru - c019 - p001.jpg", Page("second")));

        var source = Assert.Single(ComicSourceScanner.Scan(_root));
        Assert.Equal("Narutaru_vol.03.cbz", source.Name);
        Assert.Equal(ComicSourceKind.Repack, source.Kind);

        var target = At("out", source.Name);
        ComicSourceConverter.Materialize(source, target);

        // Page names carry the chapter markers a compilation is linked by, so they survive the
        // rebuild untouched rather than being renumbered.
        Assert.Equal(
            ["Narutaru - c018 - p001.jpg", "Narutaru - c019 - p001.jpg"],
            CbzReader.PageNames(target));

        using var stream = CbzReader.OpenPage(target, "Narutaru - c018 - p001.jpg");
        using var reader = new StreamReader(stream!);
        Assert.Equal("first", reader.ReadToEnd());
    }

    // One failed grab was ten .cbr, each holding one .rar per chapter.
    [Fact]
    public void An_archive_of_archives_yields_one_comic_per_inner_archive()
    {
        var first = WriteTar("inner/Bokura no Hentai c001.cbt", ("c001 p001.jpg", Page("one")));
        var second = WriteTar("inner/Bokura no Hentai c002.cbt", ("c002 p001.jpg", Page("two")));
        WriteTar(
            "Bokura no Hentai v01.cbt",
            ("Bokura no Hentai c001.cbt", File.ReadAllBytes(first)),
            ("Bokura no Hentai c002.cbt", File.ReadAllBytes(second)));
        Directory.Delete(At("inner"), recursive: true);

        var sources = ComicSourceScanner.Scan(_root).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();

        Assert.Equal(
            ["Bokura no Hentai c001.cbz", "Bokura no Hentai c002.cbz"],
            sources.Select(s => s.Name));
        Assert.All(sources, s => Assert.Equal(ComicSourceKind.Repack, s.Kind));
        Assert.All(sources, s => Assert.NotNull(s.Entry));

        var target = At("out", sources[0].Name);
        ComicSourceConverter.Materialize(sources[0], target);
        Assert.Equal(["c001 p001.jpg"], CbzReader.PageNames(target));
    }

    // 2,621 loose .jpg in six per-volume folders was the largest of the failed grabs.
    [Fact]
    public void A_folder_of_loose_pages_is_one_comic_per_folder()
    {
        WriteLooseFiles("Akira/Akira v01", "001.jpg", "002.jpg");
        WriteLooseFiles("Akira/Akira v02", "001.jpg");

        var sources = ComicSourceScanner.Scan(_root).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();

        Assert.Equal(["Akira v01.cbz", "Akira v02.cbz"], sources.Select(s => s.Name));
        Assert.All(sources, s => Assert.Equal(ComicSourceKind.LooseImages, s.Kind));

        var target = At("out", sources[0].Name);
        ComicSourceConverter.Materialize(sources[0], target);
        Assert.Equal(["001.jpg", "002.jpg"], CbzReader.PageNames(target));
    }

    [Fact]
    public void Pages_beside_an_archive_belong_to_it_rather_than_forming_a_comic()
    {
        WriteZip("My Series v01.cbz", "001.jpg");
        File.WriteAllText(At("folder.jpg"), "cover");

        var source = Assert.Single(ComicSourceScanner.Scan(_root));
        Assert.Equal(ComicSourceKind.Cbz, source.Kind);
    }

    [Fact]
    public void A_convertible_file_never_displaces_the_cbz_already_made_from_it()
    {
        WriteZip("My Series v01.cbz", "001.jpg", "002.jpg");
        WriteZip("My Series v01.zip", "001.jpg");

        var source = Assert.Single(ComicSourceScanner.Scan(_root));

        Assert.Equal(ComicSourceKind.Cbz, source.Kind);
        Assert.Equal(2, source.Pages.Count);
    }

    // ".cbz" on a tar body: the extension says zip, the bytes say otherwise, and the bytes win.
    [Fact]
    public void An_archive_is_typed_by_its_bytes_not_its_extension()
    {
        WriteTar("Disguised v01.cbz", ("Disguised - c001 - p001.jpg", Page("first")));
        WriteZip("Honest v02.rar", "001.jpg");

        var sources = ComicSourceScanner.Scan(_root).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();

        Assert.Equal(["Disguised v01.cbz", "Honest v02.cbz"], sources.Select(s => s.Name));
        Assert.Equal(ComicSourceKind.Repack, sources[0].Kind);
        Assert.Equal(ComicSourceKind.Zip, sources[1].Kind);

        var target = At("out", sources[0].Name);
        ComicSourceConverter.Materialize(sources[0], target);
        Assert.Equal(ArchiveSignature.Container.Zip, ArchiveSignature.Sniff(target));
        Assert.Equal(["Disguised - c001 - p001.jpg"], CbzReader.PageNames(target));
    }

    // A library import builds the CBZ in the folder the file already sits in, so the target is the
    // mislabelled file's own path. It used to see that name taken and link the 7z unread.
    [Fact]
    public void A_mislabelled_archive_is_rebuilt_under_its_own_name()
    {
        var original = WriteTar("Blue Period Ch.4.cbz", ("001.jpg", Page("first")));
        var originalBytes = File.ReadAllBytes(original);
        var source = Assert.Single(ComicSourceScanner.Scan(_root));

        ComicSourceConverter.Materialize(source, At(source.Name));

        Assert.Equal(ArchiveSignature.Container.Zip, ArchiveSignature.Sniff(original));
        Assert.Equal(["001.jpg"], CbzReader.PageNames(original));
        Assert.Equal(originalBytes, File.ReadAllBytes(At("Blue Period Ch.4.cbt")));
    }

    [Fact]
    public void Repacking_in_place_leaves_a_real_zip_alone()
    {
        var zip = WriteZip("Honest Ch.1.cbz", "001.jpg");

        Assert.False(ComicSourceConverter.RepackMislabelled(zip));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void Repacking_in_place_names_the_original_after_its_container()
    {
        var path = WriteTar("Disguised Ch.2.cbz", ("001.jpg", Page("first")));

        Assert.True(ComicSourceConverter.RepackMislabelled(path));

        Assert.Equal(["001.jpg"], CbzReader.PageNames(path));
        Assert.True(File.Exists(At("Disguised Ch.2.cbt")));
    }

    [Theory]
    [InlineData(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04 }, ".cb7")]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }, ".cbr")]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 }, null)]
    [InlineData(new byte[0], null)]
    public void A_container_names_its_comic_extension(byte[] head, string? expected) =>
        Assert.Equal(expected, ArchiveSignature.ComicExtension(ArchiveSignature.Detect(head)));

    [Theory]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00 }, ArchiveSignature.Container.Zip)]
    [InlineData(new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04 }, ArchiveSignature.Container.Other)]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }, ArchiveSignature.Container.Other)]
    [InlineData(new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 }, ArchiveSignature.Container.Other)]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46 }, ArchiveSignature.Container.Unknown)]
    [InlineData(new byte[0], ArchiveSignature.Container.Unknown)]
    public void Signatures_are_read_off_the_leading_bytes(byte[] head, ArchiveSignature.Container expected) =>
        Assert.Equal(expected, ArchiveSignature.Sniff(head));

    [Fact]
    public void An_archive_holding_nothing_readable_is_not_a_comic()
    {
        WriteTar("Notes.cbt", ("readme.txt", Page("nothing to see")));

        Assert.Empty(ComicSourceScanner.Scan(_root));
    }

    [Fact]
    public void The_failure_message_says_what_was_found()
    {
        WriteLooseFiles("scans", "a.txt", "b.txt", "c.pdf");

        Assert.Equal("found 2 .txt, 1 .pdf", ComicSourceScanner.Describe(_root));
        Assert.Equal("it is empty", ComicSourceScanner.Describe(At("nothing")));
    }
}
