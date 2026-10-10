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
    public void Fills_scan_information_and_age_rating_from_the_stored_values()
    {
        var path = CreateCbz("Berserk v01.cbz", comicInfoXml: null);
        var series = TestSeries();
        series.ContentRating = "suggestive";

        ComicInfoUpdater.UpdateFile(path, series, ReleaseNameParser.ParseFileName(path), null, "Group Name");

        var info = ReadComicInfo(path);
        Assert.Equal("Group Name", info.ScanInformation);
        Assert.Equal("Mature 17+", info.AgeRating);
    }

    [Fact]
    public void Keeps_existing_scan_information_and_age_rating_when_maki_has_no_value()
    {
        var path = CreateCbz("Berserk v01.cbz", """
            <?xml version="1.0"?>
            <ComicInfo>
              <ScanInformation>Old Group</ScanInformation>
              <AgeRating>Teen</AgeRating>
            </ComicInfo>
            """);

        ComicInfoUpdater.UpdateFile(path, TestSeries(), ReleaseNameParser.ParseFileName(path), null, group: null);

        var info = ReadComicInfo(path);
        Assert.Equal("Old Group", info.ScanInformation);
        Assert.Equal("Teen", info.AgeRating);
    }

    [Fact]
    public void A_known_group_replaces_the_scan_information_a_file_declared()
    {
        var path = CreateCbz("Berserk v01.cbz", """
            <?xml version="1.0"?>
            <ComicInfo>
              <ScanInformation>Old Group</ScanInformation>
            </ComicInfo>
            """);

        ComicInfoUpdater.UpdateFile(path, TestSeries(), ReleaseNameParser.ParseFileName(path), null, "New Group");

        Assert.Equal("New Group", ReadComicInfo(path).ScanInformation);
    }

    [Fact]
    public void Keeps_the_existing_fields_of_a_file_that_declares_utf16()
    {
        var path = CreateCbz("Berserk v01.cbz", """
            <?xml version="1.0" encoding="utf-16"?>
            <ComicInfo>
              <Publisher>Dark Horse</Publisher>
            </ComicInfo>
            """);

        Assert.True(ComicInfoUpdater.UpdateFile(path, TestSeries(), ReleaseNameParser.ParseFileName(path), null));

        Assert.Equal("Dark Horse", ReadComicInfo(path).Publisher);
    }

    [Theory]
    [InlineData("manga", "YesAndRightToLeft")]
    [InlineData("manhwa", "Yes")]
    [InlineData("manhua", "Yes")]
    [InlineData("oel", "No")]
    public void Reading_direction_follows_the_series_type(string type, string expected)
    {
        var path = CreateCbz("Berserk v01.cbz", "<ComicInfo><Manga>YesAndRightToLeft</Manga></ComicInfo>");
        var series = TestSeries();
        series.Type = type;

        ComicInfoUpdater.UpdateFile(path, series, ReleaseNameParser.ParseFileName(path), null);

        Assert.Equal(expected, ReadComicInfo(path).Manga);
    }

    [Fact]
    public void An_unknown_type_keeps_the_direction_the_file_declared()
    {
        var path = CreateCbz("Berserk v01.cbz", "<ComicInfo><Manga>No</Manga></ComicInfo>");

        ComicInfoUpdater.UpdateFile(path, TestSeries(), ReleaseNameParser.ParseFileName(path), null);

        Assert.Equal("No", ReadComicInfo(path).Manga);
    }

    [Fact]
    public void A_differently_cased_modelled_element_is_not_written_twice()
    {
        var path = CreateCbz("Berserk v01.cbz", "<ComicInfo><summary>Old</summary><Characters>Guts</Characters></ComicInfo>");

        ComicInfoUpdater.UpdateFile(path, TestSeries(), ReleaseNameParser.ParseFileName(path), null);

        using var archive = ZipFile.OpenRead(path);
        using var reader = new StreamReader(archive.Entries.Single(e => e.Name == "ComicInfo.xml").Open());
        var root = System.Xml.Linq.XDocument.Parse(reader.ReadToEnd()).Root!;
        Assert.Equal("Dark fantasy.", Assert.Single(root.Elements().Where(e => string.Equals(e.Name.LocalName, "summary", StringComparison.OrdinalIgnoreCase))).Value);
        Assert.Equal("Guts", root.Element("Characters")!.Value);
    }

    [Fact]
    public void A_differently_cased_element_fills_an_empty_modelled_field_before_it_is_dropped()
    {
        var path = CreateCbz("Berserk v01.cbz", "<ComicInfo><publisher>Dark Horse</publisher><TRANSLATOR>Duane</TRANSLATOR></ComicInfo>");

        ComicInfoUpdater.UpdateFile(path, TestSeries(), ReleaseNameParser.ParseFileName(path), null);

        var info = ReadComicInfo(path);
        Assert.Equal("Dark Horse", info.Publisher);
        Assert.Equal("Duane", info.Translator);
        using var archive = ZipFile.OpenRead(path);
        using var reader = new StreamReader(archive.Entries.Single(e => e.Name == "ComicInfo.xml").Open());
        var root = System.Xml.Linq.XDocument.Parse(reader.ReadToEnd()).Root!;
        Assert.Single(root.Elements().Where(e => e.Name.LocalName.Equals("publisher", StringComparison.OrdinalIgnoreCase)));
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

    private static Series CompletedSeries(int? totalVolumes)
    {
        var series = TestSeries();
        series.Status = SeriesStatus.Completed;
        series.TotalChapters = 119;
        series.TotalVolumes = totalVolumes;
        return series;
    }

    [Fact]
    public void Volume_file_counts_volumes()
    {
        var path = CreateCbz("Rough v12.cbz", comicInfoXml: null);

        ComicInfoUpdater.UpdateFile(path, CompletedSeries(12), ReleaseNameParser.ParseFileName(path), null);

        Assert.Equal("12", ReadComicInfo(path).CountSerialized);
    }

    [Fact]
    public void Volume_file_omits_count_when_volume_total_unknown()
    {
        var path = CreateCbz("Rough v12.cbz", "<ComicInfo><Count>119</Count></ComicInfo>");

        ComicInfoUpdater.UpdateFile(path, CompletedSeries(null), ReleaseNameParser.ParseFileName(path), null);

        Assert.Null(ReadComicInfo(path).CountSerialized);
    }

    [Fact]
    public void Chapter_file_counts_chapters()
    {
        var path = CreateCbz("Rough 119.cbz", comicInfoXml: null);
        var chapter = new Chapter { Number = 119, Volume = 12, Language = "en" };

        ComicInfoUpdater.UpdateFile(path, CompletedSeries(12), ReleaseNameParser.ParseFileName(path), chapter);

        Assert.Equal("119", ReadComicInfo(path).CountSerialized);
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

    [Fact]
    public void Fields_the_class_does_not_model_survive_the_rewrite()
    {
        var path = CreateCbz("Berserk v05.cbz", """
            <?xml version="1.0"?>
            <ComicInfo xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:noNamespaceSchemaLocation="ComicInfo.xsd">
              <Series>Beruseruku</Series>
              <Characters>Guts, Casca</Characters>
              <Teams>Band of the Hawk</Teams>
              <CommunityRating>4.5</CommunityRating>
              <Pages>
                <Page Image="0" Type="FrontCover" DoublePage="false" />
                <Page Image="4" DoublePage="true" />
              </Pages>
            </ComicInfo>
            """);

        Assert.True(ComicInfoUpdater.UpdateFile(path, TestSeries(), ReleaseNameParser.ParseFileName(path), null));

        using (var archive = ZipFile.OpenRead(path))
        {
            using var reader = new StreamReader(archive.Entries.Single(e => e.Name == "ComicInfo.xml").Open());
            var doc = System.Xml.Linq.XDocument.Parse(reader.ReadToEnd());
            var root = doc.Root!;
            Assert.Equal("Berserk", root.Element("Series")!.Value);
            Assert.Equal("Guts, Casca", root.Element("Characters")!.Value);
            Assert.Equal("Band of the Hawk", root.Element("Teams")!.Value);
            Assert.Equal("4.5", root.Element("CommunityRating")!.Value);
            var pages = root.Element("Pages")!.Elements("Page").ToList();
            Assert.Equal(2, pages.Count);
            Assert.Equal("FrontCover", pages[0].Attribute("Type")!.Value);
            Assert.Equal("true", pages[1].Attribute("DoublePage")!.Value);
        }

        Assert.False(ComicInfoUpdater.UpdateFile(path, TestSeries(), ReleaseNameParser.ParseFileName(path), null));
    }
}
