using System.IO.Compression;
using System.Text;
using Maki.Core.ComicInfo;
using Maki.Core.Entities;
using Maki.Core.Parsing;

namespace Maki.Core.Tests;

public class ComicInfoUpdaterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("maki-ci-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Series TestSeries() => new()
    {
        Title = "Berserk",
        Overview = "Dark fantasy.",
        Status = SeriesStatus.Ongoing,
        AuthorStory = "MIURA Kentaro",
        AuthorArt = "MIURA Kentaro",
        Genres = ["action", "fantasy"],
        MangaBakaId = 1692,
        FolderName = "Berserk"
    };

    private string CreateCbz(string name, string? comicInfoXml, int pages = 3)
    {
        var path = Path.Combine(_dir, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        if (comicInfoXml != null)
        {
            var entry = archive.CreateEntry("ComicInfo.xml");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(comicInfoXml);
        }

        for (var i = 1; i <= pages; i++)
        {
            var page = archive.CreateEntry($"{i:000}.jpg", CompressionLevel.NoCompression);
            using var stream = page.Open();
            stream.Write("fake image bytes"u8);
        }

        return path;
    }

    private static ComicInfo.ComicInfo ReadComicInfo(string cbzPath)
    {
        using var archive = ZipFile.OpenRead(cbzPath);
        var entry = archive.Entries.Single(e => e.Name == "ComicInfo.xml");
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        memory.Position = 0;
        return ComicInfoBuilder.Deserialize(memory)!;
    }

    [Fact]
    public void Standardizes_foreign_comicinfo_and_preserves_unmanaged_fields()
    {
        // A release group's ComicInfo: romaji series title, publisher info we must keep.
        var path = CreateCbz("Berserk v01.cbz", """
            <?xml version="1.0"?>
            <ComicInfo>
              <Series>Beruseruku</Series>
              <Title>The Black Swordsman</Title>
              <Publisher>Dark Horse</Publisher>
              <Translator>Duane Johnson</Translator>
              <Volume>1</Volume>
            </ComicInfo>
            """);

        var rewritten = ComicInfoUpdater.UpdateFile(
            path, TestSeries(), ReleaseNameParser.ParseFileName(path), chapter: null);

        Assert.True(rewritten);
        var info = ReadComicInfo(path);
        Assert.Equal("Berserk", info.Series);                 // standardized
        Assert.Equal("Dark fantasy.", info.Summary);          // standardized
        Assert.Equal("The Black Swordsman", info.Title);      // preserved
        Assert.Equal("Dark Horse", info.Publisher);           // preserved
        Assert.Equal("Duane Johnson", info.Translator);       // preserved
        Assert.Equal("1", info.VolumeSerialized);
        Assert.Equal("3", info.PageCount);
        Assert.Equal("YesAndRightToLeft", info.Manga);
    }

    [Fact]
    public void Creates_comicinfo_when_archive_has_none()
    {
        var path = CreateCbz("Berserk 010.5.cbz", comicInfoXml: null);

        var chapter = new Chapter { Number = 10.5m, Title = "The Guardians", Language = "en" };
        var rewritten = ComicInfoUpdater.UpdateFile(
            path, TestSeries(), ReleaseNameParser.ParseFileName(path), chapter);

        Assert.True(rewritten);
        var info = ReadComicInfo(path);
        Assert.Equal("Berserk", info.Series);
        Assert.Equal("10.5", info.Number);
        Assert.Equal("The Guardians", info.Title);
        Assert.Equal("en", info.LanguageISO);
    }

    [Fact]
    public void Second_pass_is_a_no_op()
    {
        var path = CreateCbz("Berserk v02.cbz", "<ComicInfo><Series>Old</Series></ComicInfo>");
        var series = TestSeries();
        var parsed = ReleaseNameParser.ParseFileName(path);

        Assert.True(ComicInfoUpdater.UpdateFile(path, series, parsed, null));
        Assert.False(ComicInfoUpdater.UpdateFile(path, series, parsed, null));
    }

    [Fact]
    public void Malformed_comicinfo_is_replaced_not_fatal()
    {
        var path = CreateCbz("Berserk v03.cbz", "<ComicInfo><Series>unclosed");

        var rewritten = ComicInfoUpdater.UpdateFile(
            path, TestSeries(), ReleaseNameParser.ParseFileName(path), null);

        Assert.True(rewritten);
        Assert.Equal("Berserk", ReadComicInfo(path).Series);
    }

    [Fact]
    public void Pages_survive_the_rewrite()
    {
        var path = CreateCbz("Berserk v04.cbz", "<ComicInfo><Series>Old</Series></ComicInfo>", pages: 5);

        ComicInfoUpdater.UpdateFile(path, TestSeries(), ReleaseNameParser.ParseFileName(path), null);

        using var archive = ZipFile.OpenRead(path);
        var pages = archive.Entries.Where(e => e.Name.EndsWith(".jpg")).OrderBy(e => e.Name).ToList();
        Assert.Equal(5, pages.Count);
        using var reader = new StreamReader(pages[0].Open());
        Assert.Equal("fake image bytes", reader.ReadToEnd());
        Assert.Single(archive.Entries, e => e.Name == "ComicInfo.xml");
    }
}
