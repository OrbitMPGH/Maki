using Maki.Core.Parsing;

namespace Maki.Core.Tests;

public class ReleaseTagsTests
{
    [Fact]
    public void Reads_group_year_and_digital_from_a_scanlation_release()
    {
        var parsed = ReleaseNameParser.ParseFileName(
            "[1r0n] I Want to End This Love Game v01 (2023) (Digital) (1r0n).cbz");

        Assert.True(ReleaseTags.IsDigital(parsed.Tags));
        Assert.Equal(2023, ReleaseTags.Year(parsed.Tags));
        Assert.Equal("1r0n", ReleaseTags.Group(parsed.Tags));
    }

    [Theory]
    [InlineData("Digital", true)]
    [InlineData("digital", true)]
    [InlineData("DIGITAL", true)]
    [InlineData("Scan", false)]
    [InlineData("Digital-HD", true)]
    [InlineData("Digital Compilation", true)]
    [InlineData("Digitally", false)]
    public void IsDigital_matches_the_exact_tag_or_a_digital_prefix_case_insensitively(string tag, bool expected)
    {
        Assert.Equal(expected, ReleaseTags.IsDigital([tag]));
    }

    [Fact]
    public void Year_reads_the_start_of_a_range_or_a_bare_year()
    {
        Assert.Equal(2023, ReleaseTags.Year(["2023-2026", "Digital"]));
        Assert.Equal(2024, ReleaseTags.Year(["Digital", "2024"]));
        Assert.Null(ReleaseTags.Year(["Digital", "Oak"]));
    }

    [Fact]
    public void Group_skips_years_resolutions_bare_letters_and_noise_words()
    {
        Assert.Equal("Danke-Empire", ReleaseTags.Group(["2024", "1920x2880", "f", "Digital", "Danke-Empire"]));
    }

    [Fact]
    public void Group_treats_a_publisher_name_as_a_group()
    {
        Assert.Equal("Kobo", ReleaseTags.Group(["2024", "Digital", "Kobo"]));
        Assert.Equal("Amazon", ReleaseTags.Group(["Amazon"]));
        Assert.Equal("Yen Press", ReleaseTags.Group(["Yen Press"]));
    }

    [Fact]
    public void Group_is_null_when_only_noise_remains()
    {
        Assert.Null(ReleaseTags.Group(["2024", "Digital", "f"]));
        Assert.Null(ReleaseTags.Group([]));
    }

    [Theory]
    [InlineData("v2")]
    [InlineData("c05")]
    [InlineData("Raw")]
    [InlineData("English")]
    public void Group_is_null_for_a_single_version_or_descriptive_tag(string tag)
    {
        Assert.Null(ReleaseTags.Group([tag]));
    }

    [Fact]
    public void Group_skips_version_markers_and_descriptive_words()
    {
        Assert.Null(ReleaseTags.Group(["2024", "Digital", "Uncensored"]));
        Assert.Equal("1r0n", ReleaseTags.Group(["2023", "Digital", "1r0n"]));
        Assert.Equal("Danke-Empire", ReleaseTags.Group(["Danke-Empire", "2019", "Digital"]));
    }

    [Fact]
    public void Group_returns_a_publisher_name_that_comes_before_a_version_marker()
    {
        Assert.Equal("Kobo", ReleaseTags.Group(["Kobo", "v2"]));
    }

    [Theory]
    [InlineData("Digital-HD")]
    [InlineData("c2c")]
    [InlineData("WEB-DL")]
    [InlineData("v01-v05")]
    [InlineData("Chapters 1-50")]
    [InlineData("es")]
    [InlineData("pt-br")]
    public void Group_is_null_for_release_and_language_noise(string tag)
    {
        Assert.Null(ReleaseTags.Group([tag]));
    }

    [Fact]
    public void Group_survives_uppercase_or_capitalised_group_names_that_look_like_language_tags()
    {
        Assert.Equal("TCB", ReleaseTags.Group(["TCB"]));
        Assert.Equal("1r0n", ReleaseTags.Group(["Digital-HD", "1r0n"]));
    }
}
