using Maki.Core.Entities;
using Maki.Core.Naming;

namespace Maki.Core.Tests;

public class NamingFormatterTests
{
    private static NamingContext Context(Series? series = null, Chapter? chapter = null) =>
        new(series ?? new Series { Title = "Berserk", Year = 1989, Type = SeriesTypes.Manga },
            chapter ?? new Chapter { Number = 24m, Volume = 3, Language = "en" });

    [Fact]
    public void Defaults_render_the_sample()
    {
        var sample = NamingDefaults.SampleContext();
        Assert.Equal("The Series Title's! (2010)",
            NamingFormatter.Format(NamingDefaults.SeriesFolderFormat, sample));
        Assert.Equal("The Series Title's! Vol.3 Ch.24",
            NamingFormatter.Format(NamingDefaults.ChapterFormat, sample));
    }

    [Fact]
    public void Every_token_resolves_against_the_sample()
    {
        var sample = NamingDefaults.SampleContext();
        foreach (var token in NamingTokens.All)
        {
            var example = NamingFormatter.ExampleFor(token, sample);

            // The sample chapter is numbered, so the one-shot suffix is meant to come out blank.
            if (token.Display == "{Chapter OneShotSuffix}")
            {
                Assert.Equal(string.Empty, example);
                continue;
            }

            Assert.False(string.IsNullOrWhiteSpace(example), $"{token.Display} rendered empty");
            Assert.Equal(example.Trim(), example);
        }
    }

    [Fact]
    public void Chapter_tokens_are_empty_without_a_chapter()
    {
        var context = new NamingContext(new Series { Title = "Berserk" });
        Assert.Equal("Berserk", NamingFormatter.Format("{Series Title} {Chapter VolChap}", context));
    }

    [Fact]
    public void Missing_year_leaves_no_empty_brackets()
    {
        var context = Context(new Series { Title = "Berserk" });
        Assert.Equal("Berserk", NamingFormatter.Format("{Series TitleYear}", context));
    }

    [Fact]
    public void Missing_volume_drops_the_volume_half()
    {
        var context = Context(chapter: new Chapter { Number = 10.5m });
        Assert.Equal("Berserk Ch.10.5", NamingFormatter.Format(NamingDefaults.ChapterFormat, context));
    }

    [Fact]
    public void Empty_token_between_literals_leaves_no_stray_punctuation()
    {
        // OriginalTitle is null here, so the " - " has nothing to join.
        var context = Context();
        Assert.Equal("Berserk", NamingFormatter.Format("{Series Title} - {Series OriginalTitle}", context));
    }

    [Fact]
    public void Empty_token_between_two_values_does_not_double_the_spaces()
    {
        var context = Context(chapter: new Chapter { Number = 24m });
        Assert.Equal("Berserk 24 en",
            NamingFormatter.Format("{Series Title} {Chapter Volume} {Chapter Number} {Chapter Language}",
                Context(chapter: new Chapter { Number = 24m, Language = "en" })));
        Assert.Equal("Berserk Ch.24", NamingFormatter.Format(NamingDefaults.ChapterFormat, context));
    }

    [Theory]
    [InlineData("{Series Title}", "The Series Title's!")]
    [InlineData("{Series.Title}", "The.Series.Title's!")]
    [InlineData("{series title}", "the series title's!")]
    [InlineData("{SERIES TITLE}", "THE SERIES TITLE'S!")]
    [InlineData("{series_title}", "the_series_title's!")]
    public void Token_spelling_drives_separator_and_case(string format, string expected)
    {
        Assert.Equal(expected, NamingFormatter.Format(format, NamingDefaults.SampleContext()));
    }

    [Theory]
    [InlineData(24, "{Chapter Number:000}", "024")]
    [InlineData(10.5, "{Chapter Number:000}", "010.5")]
    [InlineData(24, "{Chapter Number}", "24")]
    public void Padding_widens_only_the_integer_part(decimal number, string format, string expected)
    {
        var context = Context(chapter: new Chapter { Number = number });
        Assert.Equal(expected, NamingFormatter.Format(format, context));
    }

    [Fact]
    public void Volume_padding_is_honoured()
    {
        Assert.Equal("003", NamingFormatter.Format("{Chapter Volume:000}", Context()));
    }

    [Fact]
    public void Illegal_characters_are_stripped_from_the_result()
    {
        var context = Context(new Series { Title = "Re:Zero? <Test>" });
        Assert.Equal("ReZero Test Ch.24", NamingFormatter.Format("{Series Title} {Chapter VolChap}",
            Context(new Series { Title = "Re:Zero? <Test>" }, new Chapter { Number = 24m })));
        Assert.Equal("ReZero Test", NamingFormatter.Format("{Series Title}", context));
    }

    [Fact]
    public void CleanTitle_drops_punctuation()
    {
        Assert.Equal("The Series Titles",
            NamingFormatter.Format("{Series CleanTitle}", NamingDefaults.SampleContext()));
    }

    [Fact]
    public void One_shot_suffix_only_fires_for_a_differently_titled_one_shot()
    {
        var series = new Series { Title = "Look Back" };
        Assert.Equal("Look Back", NamingFormatter.Format(NamingDefaults.ChapterFormat,
            new NamingContext(series, new Chapter { IsOneShot = true })));
        Assert.Equal("Look Back", NamingFormatter.Format(NamingDefaults.ChapterFormat,
            new NamingContext(series, new Chapter { IsOneShot = true, Title = "Look Back" })));
        Assert.Equal("Look Back - Bonus", NamingFormatter.Format(NamingDefaults.ChapterFormat,
            new NamingContext(series, new Chapter { IsOneShot = true, Title = "Bonus" })));
    }

    [Fact]
    public void Ids_render_and_vanish_when_unmatched()
    {
        Assert.Equal("The Series Title's! [12345]",
            NamingFormatter.Format("{Series Title} [{MangaBakaId}]", NamingDefaults.SampleContext()));
        Assert.Equal("Berserk", NamingFormatter.Format("{Series Title} [{MangaBakaId}]", Context()));
    }

    [Fact]
    public void Valid_formats_pass_validation()
    {
        Assert.Empty(NamingFormatter.Validate(NamingDefaults.SeriesFolderFormat));
        Assert.Empty(NamingFormatter.Validate(NamingDefaults.ChapterFormat));
        Assert.Empty(NamingFormatter.Validate("{Series Title} ({Series Year}) {Chapter Number:000}"));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData("{Nonsense Token}", "Unknown token")]
    [InlineData("Just a literal", "at least one token")]
    [InlineData("{Series Title}/{Chapter Number}", "path separators")]
    [InlineData("{Series Title}\\x", "path separators")]
    [InlineData("../{Series Title}", "\"..\"")]
    [InlineData("{Series {Title}}", "inside a token")]
    [InlineData("{Series Title", "no matching")]
    [InlineData("Series Title}", "no matching")]
    [InlineData("{Series Title:000}", "does not take a padding")]
    [InlineData("{Chapter Number:abc}", "must be zeroes")]
    public void Invalid_formats_are_refused(string format, string expectedFragment)
    {
        var errors = NamingFormatter.Validate(format);
        Assert.Contains(errors, e => e.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Unknown_token_is_dropped_rather_than_written_into_a_name()
    {
        // Can't be saved, but a format stored before a token was removed still has to name a file.
        Assert.Equal("Berserk", NamingFormatter.Format("{Series Title} {Gone Away}", Context()));
    }
}
