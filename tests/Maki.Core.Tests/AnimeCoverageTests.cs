using System.Globalization;
using Maki.Core.Recommendations;

namespace Maki.Core.Tests;

/// <summary>
/// The C# port of frontend/src/lib/animeCoverage.ts. The strings come from the TS doc comments and
/// from MangaBaka entries that broke earlier parsers.
/// </summary>
public class AnimeCoverageTests
{
    [Fact]
    public void Doc_comment_example_parses_every_labelled_marker()
    {
        var spans = AnimeCoverage.Parse(
            "Vol 1, Chap 1 (S1) / Vol 31, Chap 270 (Film + OVA) / Vol 35, Chap 315 (S2)", null);

        Assert.Equal(3, spans.Count);
        Assert.Equal(new AnimeSpan("S1", 1m, null, true, AnimeSpanKind.Season), spans[0]);
        Assert.Equal(new AnimeSpan("Film + OVA", 270m, null, true, AnimeSpanKind.Film), spans[1]);
        Assert.Equal(new AnimeSpan("S2", 315m, null, true, AnimeSpanKind.Season), spans[2]);
    }

    [Fact]
    public void Same_label_on_both_sides_pairs_into_a_closed_span()
    {
        var spans = AnimeCoverage.Parse("Chap 1 (S1)", "Chap 270 (S1)");

        Assert.Equal(new AnimeSpan("S1", 1m, 270m, false, AnimeSpanKind.Season, "S1"), Assert.Single(spans));
    }

    [Fact]
    public void Entry_with_no_chapter_anchor_yields_nothing()
    {
        Assert.Empty(AnimeCoverage.Parse("Alternate Setting with an original ending", null));
        Assert.Empty(AnimeCoverage.Parse(null, "Alternate Setting with an original ending"));
        Assert.Empty(AnimeCoverage.Parse(null, null));
        Assert.Empty(AnimeCoverage.Parse("", ""));
    }

    [Fact]
    public void Trailing_note_after_the_last_label_is_ignored()
    {
        var spans = AnimeCoverage.Parse(
            "Vol 1, Chap 1 (Naruto) / Vol 28, Chap 245 (Shippuden)",
            "Vol 27, Chap 238 (Naruto) / Vol 72, Chap 700 (Shippuden) Chap 239-244 adapted in EP 119-120");

        Assert.Equal(2, spans.Count);
        Assert.Equal(new AnimeSpan("Naruto", 1m, 238m, false, AnimeSpanKind.Other, "Naruto"), spans[0]);
        Assert.Equal(new AnimeSpan("Shippuden", 245m, 700m, false, AnimeSpanKind.Season, "Shippuden"), spans[1]);
    }

    [Fact]
    public void No_label_anywhere_falls_back_to_the_first_chapter_labelled_S1()
    {
        var spans = AnimeCoverage.Parse("Vol 1, Chap 1", "Vol 10, Chap 85 / Vol 12, Chap 99");

        Assert.Equal(new AnimeSpan("S1", 1m, 85m, false, AnimeSpanKind.Season), Assert.Single(spans));
    }

    [Fact]
    public void Labels_pair_before_position()
    {
        var spans = AnimeCoverage.Parse("Chap 1 (S1) / Chap 100 (S2)", "Chap 150 (S2) / Chap 90 (S1)");

        Assert.Equal(2, spans.Count);
        Assert.Equal(new AnimeSpan("S1", 1m, 90m, false, AnimeSpanKind.Season, "S1"), spans[0]);
        Assert.Equal(new AnimeSpan("S2", 100m, 150m, false, AnimeSpanKind.Season, "S2"), spans[1]);
    }

    [Fact]
    public void Mismatched_labels_fall_back_to_the_next_unused_end()
    {
        var spans = AnimeCoverage.Parse("Chap 1 (Season 1) / Chap 60 (Season 2)", "Chap 50 (Part 1) / Chap 120 (Part 2)");

        Assert.Equal(2, spans.Count);
        Assert.Equal(new AnimeSpan("Season 1", 1m, 50m, false, AnimeSpanKind.Season, "Part 1"), spans[0]);
        Assert.Equal(new AnimeSpan("Season 2", 60m, 120m, false, AnimeSpanKind.Season, "Part 2"), spans[1]);
    }

    [Fact]
    public void Label_matching_ignores_case_and_whitespace()
    {
        var spans = AnimeCoverage.Parse("Chap 1 ( Season 1 )", "Chap 30 (Part 1) / Chap 40 (season 1)");

        Assert.Equal(new AnimeSpan("Season 1", 1m, 40m, false, AnimeSpanKind.Season, "season 1"), Assert.Single(spans));
    }

    [Fact]
    public void Start_without_an_end_is_open_ended()
    {
        var spans = AnimeCoverage.Parse("Chap 1 (S1) / Chap 51 (S2)", "Chap 50 (S1)");

        Assert.Equal(2, spans.Count);
        Assert.Equal(new AnimeSpan("S1", 1m, 50m, false, AnimeSpanKind.Season, "S1"), spans[0]);
        Assert.Equal(new AnimeSpan("S2", 51m, null, true, AnimeSpanKind.Season), spans[1]);
    }

    [Fact]
    public void Part_labelled_ends_keep_their_own_label()
    {
        var spans = AnimeCoverage.Parse(
            "Vol 1, Chap 1 (S1) / Vol 9, Chap 35 (S2) / Vol 13, Chap 51 (S3P1) / Vol 23, Chap 91 (S4P1)",
            "Vol 8, Chap 34 (S1 + OVA 1) / Vol 12, Chap 50 (S2) / Vol 22, Chap 90 (S3P2) / Vol 34, Chap 139 (S4P3)");

        Assert.Equal(
        [
            new AnimeSpan("S1", 1m, 34m, false, AnimeSpanKind.Season, "S1 + OVA 1"),
            new AnimeSpan("S2", 35m, 50m, false, AnimeSpanKind.Season, "S2"),
            new AnimeSpan("S3P1", 51m, 90m, false, AnimeSpanKind.Season, "S3P2"),
            new AnimeSpan("S4P1", 91m, 139m, false, AnimeSpanKind.Season, "S4P3"),
        ], spans);
    }

    [Fact]
    public void Unlabelled_end_leaves_the_end_label_empty()
    {
        Assert.Null(Assert.Single(AnimeCoverage.Parse("Chap 1 (S1)", "Chap 50")).EndLabel);
        Assert.Equal("S1", Assert.Single(AnimeCoverage.Parse("Chap 1", "Chap 50 (S1)")).EndLabel);
    }

    [Fact]
    public void End_landing_on_the_start_is_consumed_and_dropped()
    {
        Assert.Empty(AnimeCoverage.Parse("Chap 10 (S1)", "Chap 10 (S1)"));
    }

    [Fact]
    public void End_before_the_start_is_not_paired()
    {
        var spans = AnimeCoverage.Parse("Chap 10 (S1)", "Chap 5 (S1)");

        Assert.Equal(new AnimeSpan("S1", 10m, null, true, AnimeSpanKind.Season), Assert.Single(spans));
    }

    [Fact]
    public void Enclosing_span_sorts_before_the_one_it_contains()
    {
        var spans = AnimeCoverage.Parse("Chap 1 (Movie) / Chap 1 (S1)", "Chap 10 (Movie) / Chap 50 (S1)");

        Assert.Equal("S1", spans[0].Label);
        Assert.Equal("Movie", spans[1].Label);
    }

    [Theory]
    [InlineData("S1", AnimeSpanKind.Season)]
    [InlineData("Season 2", AnimeSpanKind.Season)]
    [InlineData("Shippuden", AnimeSpanKind.Season)]
    [InlineData("Part 2", AnimeSpanKind.Season)]
    [InlineData("Film + OVA", AnimeSpanKind.Film)]
    [InlineData("Mugen Train Movie", AnimeSpanKind.Film)]
    [InlineData("Specials", AnimeSpanKind.Film)]
    [InlineData("ONA", AnimeSpanKind.Film)]
    [InlineData("Brotherhood", AnimeSpanKind.Other)]
    [InlineData("Naruto", AnimeSpanKind.Other)]
    public void Kind_reads_off_the_label(string label, AnimeSpanKind expected)
    {
        Assert.Equal(expected, AnimeCoverage.KindOf(label));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public void Decimal_chapters_and_kinds_do_not_depend_on_thread_culture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            CultureInfo.CurrentUICulture = new CultureInfo(culture);

            var spans = AnimeCoverage.Parse("CHAP 12.5 (S1) / Chap 40.5 (FILM)", "chap 30.5 (s1) / Chap 45 (FILM)");

            Assert.Equal(2, spans.Count);
            Assert.Equal(new AnimeSpan("S1", 12.5m, 30.5m, false, AnimeSpanKind.Season, "s1"), spans[0]);
            Assert.Equal(new AnimeSpan("FILM", 40.5m, 45m, false, AnimeSpanKind.Film, "FILM"), spans[1]);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }
}
