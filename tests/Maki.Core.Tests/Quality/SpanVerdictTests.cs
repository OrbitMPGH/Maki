using Maki.Core.Parsing;
using Maki.Core.Quality;

namespace Maki.Core.Tests.Quality;

public class SpanVerdictTests
{
    private const long Mb = 1024 * 1024;
    private const long Budget = 500 * Mb;

    private static readonly ReleaseSpan Volume3 = new(new NumberRange(3, 3), []);

    private static SpanChapter Upgrade(decimal number, int? volume, int fileId) =>
        new(number, volume, ChapterSpanState.Upgrade, fileId);

    private static SpanChapter Met(decimal number, int? volume, int fileId) =>
        new(number, volume, ChapterSpanState.AlreadyMet, fileId);

    private static SpanChapter Missing(decimal number, int? volume) =>
        new(number, volume, ChapterSpanState.Missing, null);

    private static SpanChapter Skipped(decimal number, int? volume) =>
        new(number, volume, ChapterSpanState.Skipped, null);

    private static SpanChapter Unknown(decimal number) =>
        new(number, null, ChapterSpanState.Unknown, null);

    // Volume 3 is chapters 20 to 28, every one a single-chapter aggregator file below cutoff.
    private static List<SpanChapter> Volume3AllOnDisk() =>
        [.. Enumerable.Range(20, 9).Select(n => Upgrade(n, 3, 100 + n))];

    private static SpanVerdict Evaluate(
        IReadOnlyList<SpanChapter> chapters,
        ReleaseSpan? span = null,
        bool titleMatched = true,
        bool mappingKnown = true,
        long size = 200 * Mb,
        int tolerance = 3,
        bool wholeSeries = false) =>
        SpanVerdicts.Evaluate(new SpanVerdictInput(
            span ?? Volume3, titleMatched, mappingKnown, chapters, size, Budget, tolerance, wholeSeries));

    private static readonly ReleaseSpan NoSpan = new(null, []);

    [Fact]
    public void Ten_files_against_a_six_volume_pack_is_a_proposal_even_when_every_gap_is_wanted()
    {
        var span = new ReleaseSpan(new NumberRange(1, 6), [new NumberRange(49.1m, 57)]);
        var chapters = new List<SpanChapter>();
        chapters.AddRange(Enumerable.Range(1, 10).Select(n => Upgrade(n, (n - 1) / 8 + 1, 100 + n)));
        chapters.AddRange(Enumerable.Range(11, 38).Select(n => Missing(n, (n - 1) / 8 + 1)));
        chapters.Add(Missing(49.1m, null));
        chapters.AddRange(Enumerable.Range(50, 8).Select(n => Missing(n, null)));

        var verdict = Evaluate(chapters, span, size: 1400 * Mb);

        Assert.Equal(SpanOutcome.Proposal, verdict.Outcome);
        Assert.Equal([SpanVerdictReasons.AddsMissingChapters, SpanVerdictReasons.OverBudget], verdict.Reasons);
        Assert.Equal(10, verdict.UpgradeCount);
        Assert.Equal(47, verdict.MissingCount);
        Assert.Equal(10, verdict.ReplacedFileIds.Count);
    }

    [Fact]
    public void The_six_volume_pack_is_still_a_proposal_under_budget()
    {
        var span = new ReleaseSpan(new NumberRange(1, 6), []);
        var chapters = new List<SpanChapter>();
        chapters.AddRange(Enumerable.Range(1, 10).Select(n => Upgrade(n, (n - 1) / 8 + 1, 100 + n)));
        chapters.AddRange(Enumerable.Range(11, 38).Select(n => Missing(n, (n - 1) / 8 + 1)));

        var verdict = Evaluate(chapters, span, size: 100 * Mb);

        Assert.Equal(SpanOutcome.Proposal, verdict.Outcome);
        Assert.Equal([SpanVerdictReasons.AddsMissingChapters], verdict.Reasons);
    }

    [Fact]
    public void A_single_volume_all_on_disk_below_cutoff_auto_grabs_under_budget()
    {
        var verdict = Evaluate(Volume3AllOnDisk());

        Assert.Equal(SpanOutcome.AutoGrab, verdict.Outcome);
        Assert.Empty(verdict.Reasons);
        Assert.Equal(9, verdict.UpgradeCount);
        Assert.Equal(Enumerable.Range(120, 9), verdict.ReplacedFileIds);
    }

    [Fact]
    public void The_same_volume_over_budget_is_a_proposal()
    {
        var verdict = Evaluate(Volume3AllOnDisk(), size: Budget + 1);

        Assert.Equal(SpanOutcome.Proposal, verdict.Outcome);
        Assert.Equal([SpanVerdictReasons.OverBudget], verdict.Reasons);
    }

    [Fact]
    public void Exactly_the_budget_is_not_over_it()
    {
        Assert.Equal(SpanOutcome.AutoGrab, Evaluate(Volume3AllOnDisk(), size: Budget).Outcome);
    }

    [Fact]
    public void Three_missing_chapters_in_one_volume_auto_grab()
    {
        List<SpanChapter> chapters = [.. Enumerable.Range(20, 6).Select(n => Upgrade(n, 3, 100 + n)),
            Missing(26, 3), Missing(27, 3), Missing(28, 3)];

        var verdict = Evaluate(chapters);

        Assert.Equal(SpanOutcome.AutoGrab, verdict.Outcome);
        Assert.Equal(3, verdict.MissingCount);
    }

    [Fact]
    public void Four_missing_chapters_in_one_volume_is_a_proposal()
    {
        List<SpanChapter> chapters = [.. Enumerable.Range(20, 5).Select(n => Upgrade(n, 3, 100 + n)),
            Missing(25, 3), Missing(26, 3), Missing(27, 3), Missing(28, 3)];

        var verdict = Evaluate(chapters);

        Assert.Equal(SpanOutcome.Proposal, verdict.Outcome);
        Assert.Equal([SpanVerdictReasons.AddsMissingChapters], verdict.Reasons);
        Assert.Equal(4, verdict.MissingCount);
    }

    [Fact]
    public void The_tolerance_applies_per_volume_not_across_the_span()
    {
        var span = new ReleaseSpan(new NumberRange(3, 4), []);
        List<SpanChapter> chapters = [Upgrade(20, 3, 120), Upgrade(29, 4, 129),
            Missing(21, 3), Missing(22, 3), Missing(23, 3),
            Missing(30, 4), Missing(31, 4), Missing(32, 4)];

        Assert.Equal(SpanOutcome.AutoGrab, Evaluate(chapters, span).Outcome);
    }

    [Fact]
    public void Skipped_specials_never_count_against_the_tolerance()
    {
        List<SpanChapter> chapters = [.. Volume3AllOnDisk(),
            Skipped(24.5m, 3), Skipped(25.5m, 3), Skipped(26.5m, 3), Skipped(27.5m, 3),
            Missing(29, 3)];

        var verdict = Evaluate(chapters, tolerance: 1);

        Assert.Equal(SpanOutcome.AutoGrab, verdict.Outcome);
        Assert.Equal(4, verdict.SkippedCount);
        Assert.Equal(1, verdict.MissingCount);
    }

    [Fact]
    public void A_loose_chapter_segment_not_on_disk_is_a_proposal()
    {
        var span = new ReleaseSpan(new NumberRange(3, 3), [new NumberRange(29, 30)]);
        List<SpanChapter> chapters = [.. Volume3AllOnDisk(), Missing(29, null), Upgrade(30, null, 130)];

        var verdict = Evaluate(chapters, span);

        Assert.Equal(SpanOutcome.Proposal, verdict.Outcome);
        Assert.Equal([SpanVerdictReasons.AddsMissingChapters], verdict.Reasons);
    }

    [Fact]
    public void A_missing_chapter_inside_a_segment_counts_even_when_it_has_a_volume()
    {
        var span = new ReleaseSpan(new NumberRange(3, 3), [new NumberRange(28, 30)]);
        List<SpanChapter> chapters = [Upgrade(20, 3, 120), Missing(28, 3)];

        Assert.Equal(SpanOutcome.Proposal, Evaluate(chapters, span, tolerance: 10).Outcome);
    }

    [Fact]
    public void A_chapter_segment_entirely_on_disk_auto_grabs()
    {
        var span = new ReleaseSpan(null, [new NumberRange(20, 28)]);
        List<SpanChapter> chapters = [.. Enumerable.Range(20, 9).Select(n => Upgrade(n, null, 100 + n))];

        Assert.Equal(SpanOutcome.AutoGrab, Evaluate(chapters, span, mappingKnown: false).Outcome);
    }

    [Fact]
    public void An_unknown_volume_mapping_is_a_proposal()
    {
        var verdict = Evaluate(Volume3AllOnDisk(), mappingKnown: false);

        Assert.Equal(SpanOutcome.Proposal, verdict.Outcome);
        Assert.Equal([SpanVerdictReasons.VolumeSpanUnknown], verdict.Reasons);
    }

    [Fact]
    public void Unknown_chapters_are_a_proposal()
    {
        var span = new ReleaseSpan(null, [new NumberRange(20, 30)]);
        List<SpanChapter> chapters = [.. Enumerable.Range(20, 9).Select(n => Upgrade(n, null, 100 + n)),
            Unknown(29), Unknown(30)];

        var verdict = Evaluate(chapters, span);

        Assert.Equal(SpanOutcome.Proposal, verdict.Outcome);
        Assert.Equal([SpanVerdictReasons.UnknownChapters], verdict.Reasons);
        Assert.Equal(2, verdict.UnknownCount);
    }

    [Fact]
    public void Proposal_reasons_accumulate_in_order()
    {
        List<SpanChapter> chapters = [Upgrade(20, 3, 120), Unknown(40),
            Missing(21, 3), Missing(22, 3), Missing(23, 3), Missing(24, 3)];

        var verdict = Evaluate(chapters, mappingKnown: false, size: Budget * 2);

        Assert.Equal(
            [SpanVerdictReasons.UnknownChapters, SpanVerdictReasons.VolumeSpanUnknown,
                SpanVerdictReasons.AddsMissingChapters, SpanVerdictReasons.OverBudget],
            verdict.Reasons);
    }

    [Fact]
    public void A_pack_improving_nothing_on_disk_is_ignored()
    {
        List<SpanChapter> chapters = [.. Enumerable.Range(20, 9).Select(n => Met(n, 3, 100 + n))];

        var verdict = Evaluate(chapters);

        Assert.Equal(SpanOutcome.Ignore, verdict.Outcome);
        Assert.Equal([SpanVerdictReasons.NothingToUpgrade], verdict.Reasons);
        Assert.Equal(9, verdict.AlreadyMetCount);
        Assert.Empty(verdict.ReplacedFileIds);
    }

    [Fact]
    public void A_title_below_threshold_is_ignored_whatever_it_would_improve()
    {
        var verdict = Evaluate(Volume3AllOnDisk(), titleMatched: false);

        Assert.Equal(SpanOutcome.Ignore, verdict.Outcome);
        Assert.Equal([SpanVerdictReasons.TitleUncertain], verdict.Reasons);
        Assert.Equal(9, verdict.UpgradeCount);
    }

    [Fact]
    public void An_empty_span_is_ignored()
    {
        var verdict = Evaluate(Volume3AllOnDisk(), span: new ReleaseSpan(null, []));

        Assert.Equal(SpanOutcome.Ignore, verdict.Outcome);
        Assert.Equal([SpanVerdictReasons.NoSpan], verdict.Reasons);
    }

    [Fact]
    public void A_library_that_stops_short_of_the_volume_end_still_auto_grabs()
    {
        List<SpanChapter> chapters = [.. Enumerable.Range(20, 7).Select(n => Upgrade(n, 3, 100 + n)),
            Skipped(24.5m, 3), Missing(27, 3), Missing(28, 3)];

        Assert.Equal(SpanOutcome.AutoGrab, Evaluate(chapters).Outcome);
    }

    [Fact]
    public void Counts_and_replaced_file_ids_are_filled()
    {
        // Chapters 20 and 21 share one two-chapter file; 22 is at cutoff already.
        List<SpanChapter> chapters = [Upgrade(20, 3, 7), Upgrade(21, 3, 7), Upgrade(23, 3, 9), Met(22, 3, 8),
            Skipped(22.5m, 3), Missing(24, 3), Unknown(25)];

        var verdict = Evaluate(chapters);

        Assert.Equal(3, verdict.UpgradeCount);
        Assert.Equal(1, verdict.AlreadyMetCount);
        Assert.Equal(1, verdict.SkippedCount);
        Assert.Equal(1, verdict.MissingCount);
        Assert.Equal(1, verdict.UnknownCount);
        Assert.Equal([7, 9], verdict.ReplacedFileIds);
    }

    [Fact]
    public void A_whole_series_pack_is_a_proposal_even_under_budget_with_nothing_missing()
    {
        var verdict = Evaluate(Volume3AllOnDisk(), NoSpan, size: 10 * Mb, tolerance: 50, wholeSeries: true);

        Assert.Equal(SpanOutcome.Proposal, verdict.Outcome);
        Assert.Equal([SpanVerdictReasons.WholeSeriesPack], verdict.Reasons);
        Assert.Equal(9, verdict.UpgradeCount);
    }

    [Fact]
    public void A_whole_series_pack_collects_the_other_reasons_too()
    {
        List<SpanChapter> chapters = [Upgrade(1, null, 1), Missing(2, null), Missing(3, 1)];

        var verdict = Evaluate(chapters, NoSpan, size: 900 * Mb, tolerance: 50, wholeSeries: true);

        Assert.Equal(SpanOutcome.Proposal, verdict.Outcome);
        Assert.Equal([SpanVerdictReasons.WholeSeriesPack, SpanVerdictReasons.AddsMissingChapters,
            SpanVerdictReasons.OverBudget], verdict.Reasons);
    }

    [Fact]
    public void A_whole_series_pack_still_ignores_an_uncertain_title_or_nothing_to_upgrade()
    {
        var uncertain = Evaluate(Volume3AllOnDisk(), NoSpan, titleMatched: false, wholeSeries: true);
        var nothing = Evaluate([Met(1, null, 1), Missing(2, null)], NoSpan, wholeSeries: true);

        Assert.Equal(SpanOutcome.Ignore, uncertain.Outcome);
        Assert.Equal([SpanVerdictReasons.TitleUncertain], uncertain.Reasons);
        Assert.Equal(SpanOutcome.Ignore, nothing.Outcome);
        Assert.Equal([SpanVerdictReasons.NothingToUpgrade], nothing.Reasons);
    }

    [Fact]
    public void The_same_chapters_without_the_whole_series_flag_are_ignored_for_no_span()
    {
        var verdict = Evaluate(Volume3AllOnDisk(), NoSpan);

        Assert.Equal([SpanVerdictReasons.NoSpan], verdict.Reasons);
    }
}
