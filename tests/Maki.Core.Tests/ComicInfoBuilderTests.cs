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

    // Release smoke test: MangaBaka's publisher list for Chainsaw Man carries "Panini Manga México "
    // with a trailing space, which reached ComicInfo as "... México , Devir".
    [Fact]
    public void List_fields_are_trimmed_and_joined_without_stray_spaces()
    {
        var series = TestSeries();
        series.Publisher = "MANGA Plus, Shueisha, VIZ Media, Norma Editorial, Panini Manga México , Devir";
        series.AuthorStory = " FUJIMOTO  Tatsuki ,";
        series.AuthorArt = "   ";
        series.Genres = ["action ", "", " dark  fantasy"];

        var info = ComicInfoBuilder.Build(series, new Chapter { Number = 1, Language = "en" }, pageCount: 1);

        Assert.Equal("MANGA Plus, Shueisha, VIZ Media, Norma Editorial, Panini Manga México, Devir", info.Publisher);
        Assert.Equal("FUJIMOTO Tatsuki", info.Writer);
        Assert.Null(info.Penciller);
        Assert.Equal("action, dark fantasy", info.Genre);
    }

    [Fact]
    public void Localized_series_prefers_the_alt_title_in_the_chapters_language()
    {
        var series = TestSeries();
        series.OriginalTitle = "ベルセルク";
        series.AltTitles = [new LocalizedTitle("Berserk: La Edición Definitiva", "es")];

        var spanish = ComicInfoBuilder.Build(
            series, new Chapter { Number = 1, Language = "es" }, pageCount: 1);
        Assert.Equal("Berserk: La Edición Definitiva", spanish.LocalizedSeries);

        // Nothing tagged "es-la", so this falls through to the native-script title.
        var latam = ComicInfoBuilder.Build(
            series, new Chapter { Number = 1, Language = "es-la" }, pageCount: 1);
        Assert.Equal("ベルセルク", latam.LocalizedSeries);
    }

    [Fact]
    public void An_english_chapter_localizes_to_the_native_title_not_another_english_name()
    {
        // ComicInfo.Series is already the English title, so a language-matched alt title here would
        // be a second English name picked arbitrarily from however many the provider listed.
        var series = TestSeries();
        series.OriginalTitle = "ベルセルク";
        series.AltTitles = [new LocalizedTitle("Berserk: The Complete Edition", "en")];

        var info = ComicInfoBuilder.Build(
            series, new Chapter { Number = 1, Language = "en" }, pageCount: 1);

        Assert.Equal("ベルセルク", info.LocalizedSeries);
    }

    [Fact]
    public void Localized_series_is_null_when_the_series_has_no_other_name()
    {
        var info = ComicInfoBuilder.Build(
            TestSeries(), new Chapter { Number = 1, Language = "en" }, pageCount: 1);
        Assert.Null(info.LocalizedSeries);
    }

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

    [Theory]
    [InlineData("manga", "YesAndRightToLeft")]
    [InlineData("manhwa", "Yes")]
    [InlineData("manhua", "Yes")]
    [InlineData("oel", "No")]
    [InlineData(null, "YesAndRightToLeft")]
    public void Manga_reading_direction_follows_the_series_type(string? type, string expected)
    {
        var series = TestSeries();
        series.Type = type;
        Assert.Equal(expected, ComicInfoBuilder.Build(series, new Chapter { Number = 1 }, 10).Manga);
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

    [Theory]
    [InlineData("safe", "Teen")]
    [InlineData("suggestive", "Mature 17+")]
    [InlineData("erotica", "Adults Only 18+")]
    [InlineData("pornographic", "X18+")]
    [InlineData("Erotica", "Adults Only 18+")]
    [InlineData("unrated", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Content_rating_maps_to_a_comicinfo_age_rating(string? contentRating, string? expected)
    {
        var series = TestSeries();
        series.ContentRating = contentRating;

        Assert.Equal(expected, ComicInfoBuilder.Build(series, new Chapter { Number = 1 }, 10).AgeRating);
    }

    [Fact]
    public void Scan_group_is_written_as_scan_information_and_round_trips()
    {
        var info = ComicInfoBuilder.Build(TestSeries(), new Chapter { Number = 1 }, 10, group: " Group Name ");

        Assert.Equal("Group Name", info.ScanInformation);
        var xml = ComicInfoBuilder.Serialize(info);
        Assert.Contains("<ScanInformation>Group Name</ScanInformation>", xml);
        Assert.Equal("Group Name", ComicInfoBuilder.Deserialize(new StringReader(xml))!.ScanInformation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void No_scan_group_leaves_scan_information_unset(string? group)
    {
        var info = ComicInfoBuilder.Build(TestSeries(), new Chapter { Number = 1 }, 10, group);

        Assert.Null(info.ScanInformation);
        Assert.DoesNotContain("<ScanInformation", ComicInfoBuilder.Serialize(info));
    }

    [Fact]
    public void Characters_xml_cannot_carry_are_stripped_instead_of_failing_the_write()
    {
        var series = TestSeries();
        series.Overview = "Dark\u0008 fantasy\u001F with \U0001F5E1 and a lone \uD800 surrogate.";
        series.AuthorStory = "MIURA\u0001 Kentaro";
        var info = ComicInfoBuilder.Build(series, new Chapter { Number = 1, Language = "en", Title = "Bad\uFFFE" }, 10);

        var xml = ComicInfoBuilder.Serialize(info);

        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(xml);
        Assert.Equal("Dark fantasy with \U0001F5E1 and a lone  surrogate.", doc.SelectSingleNode("/ComicInfo/Summary")!.InnerText);
        Assert.Equal("MIURA Kentaro", doc.SelectSingleNode("/ComicInfo/Writer")!.InnerText);
        Assert.Equal("Bad", doc.SelectSingleNode("/ComicInfo/Title")!.InnerText);
    }
}
