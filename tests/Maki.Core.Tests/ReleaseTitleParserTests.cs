using Maki.Core.Parsing;
using Maki.Core.Quality;

namespace Maki.Core.Tests;

public class ReleaseTitleParserTests
{
    private const string WorkedExample =
        "Aishiteru Game wo Owarasetai | I Want to End This Love Game v01-06 + 049.1-057 (2023-2025) (Digital) (1r0n)";

    [Fact]
    public void The_worked_example_yields_both_halves_the_volumes_and_the_loose_chapters()
    {
        var parsed = ReleaseTitleParser.Parse(WorkedExample);

        Assert.Equal(["Aishiteru Game wo Owarasetai", "I Want to End This Love Game"], parsed.TitleCandidates);
        Assert.Equal(new NumberRange(1, 6), parsed.Span.Volumes);
        Assert.Equal([new NumberRange(49.1m, 57)], parsed.Span.ChapterSegments);
        Assert.Equal(["2023-2025", "Digital", "1r0n"], parsed.Tags);
        Assert.True(parsed.IsDigital);
        Assert.Equal("1r0n", parsed.Group);
        Assert.Equal(2023, parsed.Year);
    }

    [Fact]
    public void The_worked_example_resolves_to_the_volume_tier()
    {
        var parsed = ReleaseTitleParser.Parse(WorkedExample);

        var tier = QualityTierResolver.Resolve(null, WorkedExample, WorkedExample, isVolume: parsed.Span.Volumes is not null);

        Assert.Equal(QualityTier.Volume, tier);
    }

    [Fact]
    public void A_single_volume_has_no_chapter_segments()
    {
        var parsed = ReleaseTitleParser.Parse("Title v01 (Digital) (1r0n)");

        Assert.Equal(["Title"], parsed.TitleCandidates);
        Assert.Equal(new NumberRange(1, 1), parsed.Span.Volumes);
        Assert.Empty(parsed.Span.ChapterSegments);
        Assert.True(parsed.IsDigital);
        Assert.Equal("1r0n", parsed.Group);
    }

    [Theory]
    [InlineData("Title Vol. 1-3 [Group]", 1, 3)]
    [InlineData("Title Vol 1 [Group]", 1, 1)]
    [InlineData("Title Volume 2 [Group]", 2, 2)]
    [InlineData("Title v01-v06 [Group]", 1, 6)]
    public void Volume_markers_in_every_spelling_read_as_a_volume_span(string title, int start, int end)
    {
        var parsed = ReleaseTitleParser.Parse(title);

        Assert.Equal(["Title"], parsed.TitleCandidates);
        Assert.Equal(new NumberRange(start, end), parsed.Span.Volumes);
        Assert.Equal("Group", parsed.Group);
    }

    [Theory]
    [InlineData("Title Ch. 1-100", 1, 100)]
    [InlineData("Title ch 1-100", 1, 100)]
    [InlineData("Title Ch. 001-010", 1, 10)]
    [InlineData("Title c049.1-057", 49.1, 57)]
    public void A_chapter_range_is_one_chapter_segment(string title, double start, double end)
    {
        var parsed = ReleaseTitleParser.Parse(title);

        Assert.Equal(["Title"], parsed.TitleCandidates);
        Assert.Null(parsed.Span.Volumes);
        Assert.Equal([new NumberRange((decimal)start, (decimal)end)], parsed.Span.ChapterSegments);
    }

    [Fact]
    public void A_title_with_only_tags_has_no_span()
    {
        var parsed = ReleaseTitleParser.Parse("Title (2019) (Digital) (danke-Empire)");

        Assert.Equal(["Title"], parsed.TitleCandidates);
        Assert.True(parsed.Span.IsEmpty);
        Assert.Equal(2019, parsed.Year);
        Assert.Equal("danke-Empire", parsed.Group);
    }

    [Fact]
    public void A_year_range_tag_is_never_a_span()
    {
        var parsed = ReleaseTitleParser.Parse("Insomniacs After School (2023-2026) (Digital) (1r0n)");

        Assert.Equal(["Insomniacs After School"], parsed.TitleCandidates);
        Assert.True(parsed.Span.IsEmpty);
        Assert.Equal(["2023-2026", "Digital", "1r0n"], parsed.Tags);
    }

    [Fact]
    public void A_year_tag_after_a_volume_stays_a_tag()
    {
        var parsed = ReleaseTitleParser.Parse("Title v03 (2024)");

        Assert.Equal(["Title"], parsed.TitleCandidates);
        Assert.Equal(new NumberRange(3, 3), parsed.Span.Volumes);
        Assert.Empty(parsed.Span.ChapterSegments);
        Assert.Equal(2024, parsed.Year);
    }

    [Theory]
    [InlineData("Title v03 2024")]
    [InlineData("Title v03 2023-2025")]
    [InlineData("Title v03 1920x1080")]
    public void A_bare_year_or_resolution_after_a_volume_is_not_a_chapter(string title)
    {
        var parsed = ReleaseTitleParser.Parse(title);

        Assert.Equal(new NumberRange(3, 3), parsed.Span.Volumes);
        Assert.Empty(parsed.Span.ChapterSegments);
    }

    [Fact]
    public void A_bare_number_with_nothing_in_front_is_part_of_the_title()
    {
        var parsed = ReleaseTitleParser.Parse("Mob Psycho 100 v01");

        Assert.Equal(["Mob Psycho 100"], parsed.TitleCandidates);
        Assert.Equal(new NumberRange(1, 1), parsed.Span.Volumes);
        Assert.Empty(parsed.Span.ChapterSegments);
    }

    [Fact]
    public void Plus_joins_several_segments_and_volume_ranges_union()
    {
        var parsed = ReleaseTitleParser.Parse("Title v01-03 + v05 + c040-045 + 050");

        Assert.Equal(["Title"], parsed.TitleCandidates);
        Assert.Equal(new NumberRange(1, 5), parsed.Span.Volumes);
        Assert.Equal([new NumberRange(40, 45), new NumberRange(50, 50)], parsed.Span.ChapterSegments);
    }

    [Theory]
    [InlineData("Some Title / Other Title v02")]
    [InlineData("Some Title aka Other Title v02")]
    [InlineData("Some Title AKA Other Title v02")]
    [InlineData("Some Title|Other Title v02")]
    public void Dual_titles_split_into_two_candidates(string title)
    {
        var parsed = ReleaseTitleParser.Parse(title);

        Assert.Equal(["Some Title", "Other Title"], parsed.TitleCandidates);
        Assert.Equal(new NumberRange(2, 2), parsed.Span.Volumes);
    }

    [Fact]
    public void A_slash_inside_a_word_does_not_split()
    {
        var parsed = ReleaseTitleParser.Parse("Fate/Zero v01");

        Assert.Equal(["Fate/Zero"], parsed.TitleCandidates);
    }

    [Fact]
    public void Underscores_read_as_spaces()
    {
        var parsed = ReleaseTitleParser.Parse("Some_Title_v01-03_(Digital)_(Group)");

        Assert.Equal(["Some Title"], parsed.TitleCandidates);
        Assert.Equal(new NumberRange(1, 3), parsed.Span.Volumes);
        Assert.Equal(["Digital", "Group"], parsed.Tags);
    }

    [Fact]
    public void Trailing_punctuation_left_by_the_span_is_trimmed()
    {
        var parsed = ReleaseTitleParser.Parse("Some Title - v02 (Digital)");

        Assert.Equal(["Some Title"], parsed.TitleCandidates);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_title_parses_to_nothing(string title)
    {
        var parsed = ReleaseTitleParser.Parse(title);

        Assert.Empty(parsed.TitleCandidates);
        Assert.True(parsed.Span.IsEmpty);
        Assert.Empty(parsed.Tags);
        Assert.Null(parsed.Group);
    }

    [Theory]
    [InlineData("Boy Meets Maria (2021) (Digital) (danke-Empire)", "Boy Meets Maria")]
    [InlineData("Oh, Those Hanazono Twins (2022) (Digital) (1r0n)", "Oh, Those Hanazono Twins")]
    [InlineData("Springtime by the Window (2021) (Digital) (Shizu) [Complete]", "Springtime by the Window")]
    public void A_whole_series_pack_name_has_no_span_and_no_trailing_number(string title, string candidate)
    {
        var parsed = ReleaseTitleParser.Parse(title);

        Assert.Equal([candidate], parsed.TitleCandidates);
        Assert.True(parsed.Span.IsEmpty);
        Assert.Null(parsed.TrailingNumber);
        Assert.Null(parsed.TitleWithoutTrailingNumber);
        Assert.True(parsed.IsDigital);
    }

    [Theory]
    [InlineData("Ayakashi Triangle 001-144 (2020-2023) (Digital) (Antrill) [Completed]", 1, 144)]
    [InlineData("Ayakashi Triangle 1-144 (Digital)", 1, 144)]
    [InlineData("Ayakashi Triangle 049.1-057 (Digital)", 49.1, 57)]
    public void A_bare_range_right_before_the_tags_is_a_chapter_segment(string title, double start, double end)
    {
        var parsed = ReleaseTitleParser.Parse(title);

        Assert.Equal(["Ayakashi Triangle"], parsed.TitleCandidates);
        Assert.Null(parsed.Span.Volumes);
        Assert.Equal([new NumberRange((decimal)start, (decimal)end)], parsed.Span.ChapterSegments);
        Assert.Null(parsed.TrailingNumber);
    }

    [Theory]
    [InlineData("Title 2019-2021 (Digital)")]
    [InlineData("Title 10-5 (Digital)")]
    [InlineData("Title 1 - 5 (Digital)")]
    public void A_year_range_a_backwards_range_or_a_spaced_hyphen_is_not_a_chapter_segment(string title)
    {
        Assert.Empty(ReleaseTitleParser.Parse(title).Span.ChapterSegments);
    }

    [Fact]
    public void A_trailing_number_is_left_to_the_caller()
    {
        var parsed = ReleaseTitleParser.Parse("Ayakashi Triangle 133 (2023) (Digital) (Oak)");

        Assert.Equal(["Ayakashi Triangle 133"], parsed.TitleCandidates);
        Assert.True(parsed.Span.IsEmpty);
        Assert.Equal(133, parsed.TrailingNumber);
        Assert.Equal("Ayakashi Triangle", parsed.TitleWithoutTrailingNumber);
    }

    [Fact]
    public void A_title_ending_in_a_number_exposes_it_as_a_trailing_number()
    {
        var parsed = ReleaseTitleParser.Parse("Mob Psycho 100 (Digital)");

        Assert.Equal(["Mob Psycho 100"], parsed.TitleCandidates);
        Assert.True(parsed.Span.IsEmpty);
        Assert.Equal(100, parsed.TrailingNumber);
        Assert.Equal("Mob Psycho", parsed.TitleWithoutTrailingNumber);
    }

    [Theory]
    [InlineData("Title 2021 (Digital)")]
    [InlineData("12345 Title 12345 (Digital)")]
    [InlineData("100 (Digital)")]
    [InlineData("Title v02 5 (Digital)")]
    [InlineData("Mob Psycho 100 v01")]
    public void A_year_a_long_number_a_lone_number_or_one_after_a_span_is_no_trailing_number(string title)
    {
        Assert.Null(ReleaseTitleParser.Parse(title).TrailingNumber);
    }

    [Fact]
    public void A_trailing_range_after_another_span_is_not_a_chapter_segment()
    {
        var parsed = ReleaseTitleParser.Parse("Title v01-10 Extras 1-3 (Digital)");

        Assert.Equal(new NumberRange(1, 10), parsed.Span.Volumes);
        Assert.Empty(parsed.Span.ChapterSegments);
    }

    [Fact]
    public void A_non_blank_title_always_has_a_candidate()
    {
        var parsed = ReleaseTitleParser.Parse("v01 (Digital)");

        Assert.NotEmpty(parsed.TitleCandidates);
        Assert.Equal(new NumberRange(1, 1), parsed.Span.Volumes);
    }
}
