using Maki.Core.Entities;
using Maki.Core.Quality;
using Maki.Core.Sources;

namespace Maki.Core.Tests.Quality;

public class QualityScorerTests
{
    private static QualityCandidate Candidate(
        QualityTier tier = QualityTier.Aggregator,
        string? source = "mangadex",
        SourceKind? kind = SourceKind.Aggregator,
        string? group = "Some Group",
        string? releaseName = "Series 012.cbz",
        int? pages = 20,
        int? width = 1000,
        string? imageFormat = "jpg",
        long? size = 20_000_000,
        string? language = "en") =>
        new(tier, source, kind, group, releaseName, pages, width, imageFormat, size, language);

    private static QualityFormat Format(int id, params FormatCondition[] conditions) =>
        new() { Id = id, Name = $"f{id}", Conditions = [.. conditions] };

    private static FormatCondition Req(FormatConditionType type, string value, bool negate = false) =>
        new(type, value, Required: true, Negate: negate);

    private static FormatCondition Opt(FormatConditionType type, string value, bool negate = false) =>
        new(type, value, Required: false, Negate: negate);

    private static UpgradeProfile Profile(Action<UpgradeProfile>? configure = null)
    {
        var profile = new UpgradeProfile { UpgradesEnabled = true, Cutoff = QualityTier.Official };
        UpgradeProfileDefaults.Normalise(profile);
        configure?.Invoke(profile);
        return profile;
    }

    [Fact]
    public void Required_only_format_matches_when_every_condition_does()
    {
        var format = Format(1, Req(FormatConditionType.MinWidth, "900"), Req(FormatConditionType.MinPages, "10"));

        Assert.True(QualityScorer.Matches(format, Candidate()));
        Assert.False(QualityScorer.Matches(format, Candidate(pages: 5)));
    }

    [Fact]
    public void Optional_conditions_need_at_least_one_hit()
    {
        var format = Format(1,
            Req(FormatConditionType.MinWidth, "900"),
            Opt(FormatConditionType.ImageFormatIs, "png"),
            Opt(FormatConditionType.ImageFormatIs, "webp"));

        Assert.False(QualityScorer.Matches(format, Candidate(imageFormat: "jpg")));
        Assert.True(QualityScorer.Matches(format, Candidate(imageFormat: "webp")));
    }

    [Fact]
    public void A_failed_required_condition_beats_a_matching_optional_one()
    {
        var format = Format(1, Req(FormatConditionType.MinWidth, "1400"), Opt(FormatConditionType.ImageFormatIs, "jpg"));

        Assert.False(QualityScorer.Matches(format, Candidate(width: 1000)));
    }

    [Fact]
    public void Empty_condition_list_never_matches()
    {
        Assert.False(QualityScorer.Matches(Format(1), Candidate()));
    }

    [Fact]
    public void Negate_inverts_a_known_attribute()
    {
        var format = Format(1, Req(FormatConditionType.SourceIs, "mangadex", negate: true));

        Assert.False(QualityScorer.Matches(format, Candidate(source: "mangadex")));
        Assert.True(QualityScorer.Matches(format, Candidate(source: "mangaplus")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_null_attribute_never_matches_negated_or_not(bool negate)
    {
        var width = Format(1, Req(FormatConditionType.MinWidth, "100", negate));
        var group = Format(2, Req(FormatConditionType.GroupMatches, "x", negate));

        Assert.False(QualityScorer.Matches(width, Candidate(width: null)));
        Assert.False(QualityScorer.Matches(group, Candidate(group: null)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Invalid_regex_and_unparsable_numbers_never_match(bool negate)
    {
        Assert.False(QualityScorer.Matches(Format(1, Req(FormatConditionType.GroupMatches, "([", negate)), Candidate()));
        Assert.False(QualityScorer.Matches(Format(2, Req(FormatConditionType.MinWidth, "wide", negate)), Candidate()));
        Assert.False(QualityScorer.Matches(Format(3, Req(FormatConditionType.MinPages, "-1", negate)), Candidate()));
    }

    [Fact]
    public void A_pattern_that_times_out_never_matches_and_is_not_retried()
    {
        var regexes = new RegexCache(TimeSpan.FromMilliseconds(5));
        var format = Format(1, Req(FormatConditionType.GroupMatches, "(a+)+$"));
        var negated = Format(2, Req(FormatConditionType.GroupMatches, "(a+)+$", negate: true));
        var evil = Candidate(group: new string('a', 40) + "!");

        Assert.False(QualityScorer.Matches(format, evil, regexes));
        Assert.True(regexes.HasTimedOut("(a+)+$"));
        Assert.False(QualityScorer.Matches(negated, evil, regexes));
        Assert.False(QualityScorer.Matches(format, Candidate(group: "aaa"), regexes));
    }

    [Fact]
    public void List_values_are_case_insensitive_and_trimmed()
    {
        Assert.True(QualityScorer.Matches(Format(1, Req(FormatConditionType.SourceIs, "MangaPlus, MangaDex")), Candidate()));
        Assert.True(QualityScorer.Matches(Format(2, Req(FormatConditionType.SourceKindIs, "official,aggregator")), Candidate()));
        Assert.True(QualityScorer.Matches(Format(3, Req(FormatConditionType.ImageFormatIs, "PNG, JPG")), Candidate()));
        Assert.True(QualityScorer.Matches(Format(4, Req(FormatConditionType.LanguageIs, "EN")), Candidate()));
        Assert.False(QualityScorer.Matches(Format(5, Req(FormatConditionType.LanguageIs, "ja,ko")), Candidate()));
    }

    [Fact]
    public void Regex_conditions_are_case_insensitive()
    {
        Assert.True(QualityScorer.Matches(Format(1, Req(FormatConditionType.GroupMatches, "^some")), Candidate()));
        Assert.True(QualityScorer.Matches(Format(2, Req(FormatConditionType.ReleaseNameMatches, "SERIES \\d+")), Candidate()));
    }

    [Fact]
    public void Bytes_per_page_divides_size_by_pages_and_needs_both()
    {
        var format = Format(1, Req(FormatConditionType.MinBytesPerPage, "1000000"));

        Assert.True(QualityScorer.Matches(format, Candidate(size: 20_000_000, pages: 20)));
        Assert.False(QualityScorer.Matches(format, Candidate(size: 19_999_999, pages: 20)));
        Assert.False(QualityScorer.Matches(format, Candidate(size: 20_000_000, pages: 0)));
        Assert.False(QualityScorer.Matches(format, Candidate(size: null)));
    }

    [Fact]
    public void Score_sums_the_profile_scores_of_matching_formats()
    {
        var formats = new[]
        {
            Format(1, Req(FormatConditionType.MinWidth, "900")),
            Format(2, Req(FormatConditionType.ImageFormatIs, "jpg")),
            Format(3, Req(FormatConditionType.ImageFormatIs, "png")),
            Format(4, Req(FormatConditionType.MinPages, "1"))
        };
        var profile = Profile(p => p.FormatScores = [new(1, 10), new(2, -3), new(3, 50)]);

        var score = QualityScorer.Score(profile, formats, Candidate());

        Assert.Equal(7, score.Score);
        Assert.Equal(QualityTier.Aggregator, score.Tier);
        Assert.Equal([1, 2, 4], score.MatchedFormatIds);
    }

    [Fact]
    public void Rank_follows_a_reordered_profile()
    {
        var profile = Profile(p => p.Tiers =
        [
            new(QualityTier.Scanlator, true), new(QualityTier.Official, true), new(QualityTier.Volume, true),
            new(QualityTier.Aggregator, true), new(QualityTier.Unknown, true)
        ]);

        Assert.Equal(4, QualityScorer.Rank(profile, QualityTier.Scanlator));
        Assert.Equal(3, QualityScorer.Rank(profile, QualityTier.Official));
        Assert.Equal(0, QualityScorer.Rank(profile, QualityTier.Unknown));
        Assert.True(QualityScorer.Rank(profile, QualityTier.Scanlator) > QualityScorer.Rank(profile, QualityTier.Volume));
    }

    [Fact]
    public void Rank_of_a_missing_tier_is_zero_and_it_is_not_allowed()
    {
        var profile = new UpgradeProfile { Tiers = [new(QualityTier.Official, true), new(QualityTier.Aggregator, true)] };

        Assert.Equal(0, QualityScorer.Rank(profile, QualityTier.Volume));
        Assert.False(QualityScorer.Allows(profile, QualityTier.Volume));
    }

    [Fact]
    public void CutoffMet_compares_rank_against_the_cutoff()
    {
        var profile = Profile(p => p.Cutoff = QualityTier.Official);

        Assert.False(QualityScorer.CutoffMet(profile, QualityTier.Scanlator, 0));
        Assert.True(QualityScorer.CutoffMet(profile, QualityTier.Official, 0));
        Assert.True(QualityScorer.CutoffMet(profile, QualityTier.Volume, -100));
    }

    [Fact]
    public void CutoffMet_also_needs_the_score_when_UpgradeUntilScore_is_set()
    {
        var profile = Profile(p => { p.Cutoff = QualityTier.Official; p.UpgradeUntilScore = 10; });

        Assert.False(QualityScorer.CutoffMet(profile, QualityTier.Official, 9));
        Assert.True(QualityScorer.CutoffMet(profile, QualityTier.Official, 10));
        Assert.False(QualityScorer.CutoffMet(profile, QualityTier.Scanlator, 50));
    }

    private static QualityScore S(QualityTier tier, int score = 0) => new(tier, score, []);

    [Fact]
    public void IsUpgrade_takes_a_higher_tier()
    {
        Assert.True(QualityScorer.IsUpgrade(Profile(), S(QualityTier.Aggregator), 20, false,
            S(QualityTier.Official), 1400, 20));
    }

    [Fact]
    public void IsUpgrade_refuses_when_upgrades_are_off()
    {
        Assert.False(QualityScorer.IsUpgrade(Profile(p => p.UpgradesEnabled = false), S(QualityTier.Aggregator), 20,
            false, S(QualityTier.Official), 1400, 20));
    }

    [Fact]
    public void IsUpgrade_refuses_a_trusted_file()
    {
        Assert.False(QualityScorer.IsUpgrade(Profile(), S(QualityTier.Aggregator), 20, true,
            S(QualityTier.Official), 1400, 20));
    }

    [Fact]
    public void IsUpgrade_refuses_a_tier_the_profile_disallows()
    {
        var profile = Profile(p => p.Tiers = [.. p.Tiers.Select(t => t.Tier == QualityTier.Official ? t with { Allowed = false } : t)]);
        profile.Cutoff = QualityTier.Volume;

        Assert.False(QualityScorer.IsUpgrade(profile, S(QualityTier.Aggregator), 20, false,
            S(QualityTier.Official), 1400, 20));
    }

    [Fact]
    public void IsUpgrade_refuses_an_unmeasurable_candidate()
    {
        Assert.False(QualityScorer.IsUpgrade(Profile(), S(QualityTier.Aggregator), 20, false,
            S(QualityTier.Official), null, 20));
    }

    [Fact]
    public void IsUpgrade_refuses_a_candidate_missing_too_many_pages()
    {
        var profile = Profile(p => p.PageTolerancePercent = 10);

        Assert.False(QualityScorer.IsUpgrade(profile, S(QualityTier.Aggregator), 20, false,
            S(QualityTier.Official), 1400, 17));
        Assert.True(QualityScorer.IsUpgrade(profile, S(QualityTier.Aggregator), 20, false,
            S(QualityTier.Official), 1400, 18));
        Assert.True(QualityScorer.IsUpgrade(profile, S(QualityTier.Aggregator), null, false,
            S(QualityTier.Official), 1400, 1));
    }

    [Fact]
    public void IsUpgrade_respects_AllowReplacingUnknown()
    {
        Assert.False(QualityScorer.IsUpgrade(Profile(p => p.AllowReplacingUnknown = false), S(QualityTier.Unknown), 20,
            false, S(QualityTier.Official), 1400, 20));
        Assert.True(QualityScorer.IsUpgrade(Profile(), S(QualityTier.Unknown), 20,
            false, S(QualityTier.Official), 1400, 20));
    }

    [Fact]
    public void IsUpgrade_refuses_once_the_cutoff_is_met()
    {
        Assert.False(QualityScorer.IsUpgrade(Profile(), S(QualityTier.Official), 20, false,
            S(QualityTier.Volume), 1400, 20));
    }

    [Fact]
    public void IsUpgrade_refuses_a_lower_tier()
    {
        Assert.False(QualityScorer.IsUpgrade(Profile(), S(QualityTier.Scanlator), 20, false,
            S(QualityTier.Aggregator, 1000), 1400, 20));
    }

    [Fact]
    public void IsUpgrade_on_the_same_tier_needs_the_minimum_score_gain()
    {
        var profile = Profile(p => p.MinScoreDelta = 5);

        Assert.False(QualityScorer.IsUpgrade(profile, S(QualityTier.Scanlator, 10), 20, false,
            S(QualityTier.Scanlator, 14), 1400, 20));
        Assert.True(QualityScorer.IsUpgrade(profile, S(QualityTier.Scanlator, 10), 20, false,
            S(QualityTier.Scanlator, 15), 1400, 20));
    }

    private static QualityCandidate Listing(QualityTier tier, string source = "site", string? group = null) =>
        new(tier, source, null, group, "Series 012.cbz", null, null, null, null, "en");

    [Fact]
    public void CouldUpgrade_treats_an_unmeasured_width_as_matching_a_positive_format()
    {
        var hiRes = Format(1, Req(FormatConditionType.MinWidth, "1400"));
        var profile = Profile(p => p.FormatScores = [new FormatScore(1, 10)]);

        Assert.True(QualityScorer.CouldUpgrade(profile, [hiRes], S(QualityTier.Scanlator), 20, false,
            Listing(QualityTier.Scanlator)));
        Assert.Equal(10, QualityScorer.OptimisticScore(profile, [hiRes], Listing(QualityTier.Scanlator), new RegexCache()).Score);
    }

    [Fact]
    public void CouldUpgrade_does_not_assume_a_negative_format_away()
    {
        var hiRes = Format(1, Req(FormatConditionType.MinWidth, "1400"));
        var badGroup = Format(2, Req(FormatConditionType.GroupMatches, "^bad$"));
        var profile = Profile(p => p.FormatScores = [new FormatScore(1, 10), new FormatScore(2, -50)]);

        Assert.Equal(10, QualityScorer.OptimisticScore(profile, [hiRes, badGroup],
            Listing(QualityTier.Scanlator), new RegexCache()).Score);
        Assert.Equal(-40, QualityScorer.OptimisticScore(profile, [hiRes, badGroup],
            Listing(QualityTier.Scanlator, group: "bad"), new RegexCache()).Score);
        Assert.False(QualityScorer.CouldUpgrade(profile, [hiRes, badGroup], S(QualityTier.Scanlator), 20, false,
            Listing(QualityTier.Scanlator, group: "bad")));
    }

    [Fact]
    public void CouldUpgrade_still_fails_on_what_the_listing_already_knows()
    {
        var official = Format(1, Req(FormatConditionType.SourceIs, "mangaplus"), Req(FormatConditionType.MinWidth, "1400"));
        var profile = Profile(p => p.FormatScores = [new FormatScore(1, 10)]);

        Assert.False(QualityScorer.CouldUpgrade(profile, [official], S(QualityTier.Scanlator), 20, false,
            Listing(QualityTier.Scanlator, source: "other")));
        Assert.True(QualityScorer.CouldUpgrade(profile, [official], S(QualityTier.Scanlator), 20, false,
            Listing(QualityTier.Scanlator, source: "mangaplus")));
    }

    [Fact]
    public void CouldUpgrade_keeps_every_hard_guard()
    {
        var disallowed = Profile(p => p.Tiers = [.. p.Tiers.Select(t => t.Tier == QualityTier.Official ? t with { Allowed = false } : t)]);
        disallowed.Cutoff = QualityTier.Volume;

        Assert.True(QualityScorer.CouldUpgrade(Profile(), [], S(QualityTier.Aggregator), 20, false, Listing(QualityTier.Official)));
        Assert.False(QualityScorer.CouldUpgrade(Profile(), [], S(QualityTier.Aggregator), 20, true, Listing(QualityTier.Official)));
        Assert.False(QualityScorer.CouldUpgrade(Profile(p => p.UpgradesEnabled = false), [], S(QualityTier.Aggregator), 20,
            false, Listing(QualityTier.Official)));
        Assert.False(QualityScorer.CouldUpgrade(disallowed, [], S(QualityTier.Aggregator), 20, false, Listing(QualityTier.Official)));
        Assert.False(QualityScorer.CouldUpgrade(Profile(), [], S(QualityTier.Official), 20, false, Listing(QualityTier.Volume)));
        Assert.False(QualityScorer.CouldUpgrade(Profile(), [], S(QualityTier.Scanlator), 20, false, Listing(QualityTier.Scanlator)));
    }

    [Fact]
    public void Explain_names_the_first_guard_a_loser_trips()
    {
        var profile = Profile(p => p.PageTolerancePercent = 10);

        Assert.Equal(UpgradeReasons.Unmeasurable, UpgradeReasons.Explain(profile, 20, S(QualityTier.Official), null, 20));
        Assert.Equal(UpgradeReasons.FewerPages, UpgradeReasons.Explain(profile, 20, S(QualityTier.Official), 1400, 10));
        Assert.Equal(UpgradeReasons.ScoreNotHigher, UpgradeReasons.Explain(profile, 20, S(QualityTier.Aggregator), 1400, 20));
        profile.Tiers = [.. profile.Tiers.Select(t => t.Tier == QualityTier.Official ? t with { Allowed = false } : t)];
        Assert.Equal(UpgradeReasons.TierNotAllowed, UpgradeReasons.Explain(profile, 20, S(QualityTier.Official), 1400, 20));
    }

    [Fact]
    public void Normalise_fills_missing_tiers_in_default_order_and_drops_duplicates()
    {
        var profile = new UpgradeProfile
        {
            Tiers =
            [
                new(QualityTier.Scanlator, false), new(QualityTier.Official, true), new(QualityTier.Scanlator, true),
                new((QualityTier)42, true)
            ]
        };

        UpgradeProfileDefaults.Normalise(profile);

        Assert.Equal(
            [QualityTier.Scanlator, QualityTier.Official, QualityTier.Volume, QualityTier.Aggregator, QualityTier.Unknown],
            profile.Tiers.Select(t => t.Tier));
        Assert.False(profile.Tiers[0].Allowed);
        Assert.All(profile.Tiers.Skip(2), t => Assert.True(t.Allowed));
    }
}
