using Maki.Core.ComicInfo;
using Maki.Core.Entities;

namespace Maki.Core.Tests;

public class ComicInfoBuilderTests
{
    private static Series TestSeries(SeriesStatus status = SeriesStatus.Ongoing) => new()
    {
        Title = "Berserk",
        Overview = "Dark fantasy.",
        Status = status,
        TotalChapters = 399,
        AuthorStory = "MIURA Kentaro",
        AuthorArt = "MIURA Kentaro",
        Genres = ["action", "fantasy"],
        MangaBakaId = 1692
    };

    [Fact]
    public void Builds_expected_fields()
    {
        var chapter = new Chapter
        {
            Number = 10.5m,
            Volume = 3,
            Title = "The Guardians",
            Language = "en",
            ReleaseDate = new DateTime(2020, 5, 1)
        };

        var info = ComicInfoBuilder.Build(TestSeries(), chapter, pageCount: 20);

        Assert.Equal("Berserk", info.Series);
        Assert.Equal("The Guardians", info.Title);
        Assert.Equal("10.5", info.Number);
        Assert.Equal("3", info.VolumeSerialized);
        Assert.Null(info.CountSerialized); // not completed
        Assert.Equal("MIURA Kentaro", info.Writer);
        Assert.Equal("action, fantasy", info.Genre);
        Assert.Equal("en", info.LanguageISO);
        Assert.Equal("YesAndRightToLeft", info.Manga);
        Assert.Equal("20", info.PageCount);
        Assert.Equal("2020", info.Year);
    }

    [Fact]
    public void Count_set_when_completed()
    {
        var info = ComicInfoBuilder.Build(TestSeries(SeriesStatus.Completed), new Chapter { Number = 1 }, 10);
        Assert.Equal("399", info.CountSerialized);
    }

    [Fact]
    public void Serializes_to_valid_xml()
    {
        var info = ComicInfoBuilder.Build(TestSeries(), new Chapter { Number = 1, Language = "en" }, 10);
        var xml = ComicInfoBuilder.Serialize(info);

        Assert.StartsWith("<?xml", xml);
        Assert.Contains("<ComicInfo", xml);
        Assert.Contains("<Series>Berserk</Series>", xml);
        Assert.Contains("<Number>1</Number>", xml);

        // Must round-trip through a strict XML parser.
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(xml);
        Assert.Equal("ComicInfo", doc.DocumentElement!.Name);
    }
}
