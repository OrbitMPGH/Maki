using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Recommendations;
using Maki.Data.Identity;

namespace Maki.Api.Tests;

/// <summary>
/// How a watched anime reaches the recommender's seed space, and what it loses to whenever the
/// reader has said something stronger themselves.
/// </summary>
public class AnimeSignalSeedTests : IDisposable
{
    private readonly TestDb _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static SeedWeightService Service(IAppSettings? settings = null) => new(
        new BehavioralTasteService(TasteTuning.Default), TasteTuning.Default,
        settings ?? new FakeAppSettings());

    private void OptIn(int userId = 1, bool enabled = true)
    {
        using var db = _fixture.NewContext();
        db.UserSettings.Add(new UserSetting
        {
            UserId = userId,
            Key = SettingKeys.RecommendationsAnimeSignalsEnabled,
            Value = enabled ? "true" : "false",
        });
        db.SaveChanges();
    }

    private void SetStrength(AnimeSignalStrength strength, int userId = 1)
    {
        using var db = _fixture.NewContext();
        db.UserSettings.Add(new UserSetting
        {
            UserId = userId,
            Key = SettingKeys.RecommendationsAnimeSignalsStrength,
            Value = AnimeSignalPolicy.NameOf(strength),
        });
        db.SaveChanges();
    }

    private void Signal(long mangaBakaId, AnimeWatchStatus status, int? score,
        long animeId = 1, string service = "anilist", int userId = 1, long? malAnimeId = null)
    {
        using var db = _fixture.NewContext();
        db.AnimeSignals.Add(new AnimeSignal
        {
            UserId = userId,
            Service = service,
            AnimeId = animeId,
            MalAnimeId = malAnimeId,
            Title = $"Anime {animeId}",
            Score = score,
            Status = status,
            MangaBakaId = mangaBakaId,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task A_loved_anime_becomes_a_positive_seed_at_the_discounted_weight()
    {
        OptIn();
        Signal(500, AnimeWatchStatus.Completed, 9);

        using var db = _fixture.NewContext(1);
        var snapshot = await Service().SnapshotAsync(db, new TestCurrentUser(1));

        Assert.Contains(500L, snapshot.Effective.EligibleIds);
        Assert.Equal(0.9 * AnimeSignalPolicy.SeedScaleOf(AnimeSignalPolicy.DefaultStrength), snapshot.Effective.Weights[500], 8);
        Assert.DoesNotContain(500L, snapshot.Avoided.Keys);
        // The observed half describes the shelf, and an anime is not on it.
        Assert.DoesNotContain(500L, snapshot.Observed.EligibleIds);
        Assert.DoesNotContain(500L, snapshot.Effective.LibraryIds);
    }

    [Fact]
    public async Task A_dropped_or_low_scored_anime_joins_the_avoided_set_instead()
    {
        OptIn();
        Signal(600, AnimeWatchStatus.Dropped, null, animeId: 1);
        Signal(601, AnimeWatchStatus.Completed, 2, animeId: 2);

        using var db = _fixture.NewContext(1);
        var snapshot = await Service().SnapshotAsync(db, new TestCurrentUser(1));

        // Both discounted by the default level's share of a rating, which is what keeps a bad
        // adaptation from pushing as hard as a deliberate thumbs down.
        var share = AnimeSignalPolicy.RatingShareOf(AnimeSignalPolicy.DefaultStrength);
        Assert.Equal(AnimeSignalPolicy.DroppedStrength * share, snapshot.Avoided[600], 8);
        Assert.Equal(AnimeSignalPolicy.AvoidStrengthOfScore(2) * share, snapshot.Avoided[601], 8);
        Assert.DoesNotContain(600L, snapshot.Effective.EligibleIds);
        Assert.DoesNotContain(601L, snapshot.Effective.EligibleIds);
    }

    /// <summary>
    /// The duplicate case, end to end. A reader who scrobbles to both trackers has one viewing
    /// recorded twice, so the seed has to come out at the score they gave rather than at whatever
    /// counting the same opinion twice alongside a weaker season would produce.
    /// </summary>
    [Fact]
    public async Task One_show_listed_on_two_trackers_seeds_once()
    {
        OptIn();
        Signal(550, AnimeWatchStatus.Completed, 9, animeId: 101, service: "anilist", malAnimeId: 55);
        Signal(550, AnimeWatchStatus.Completed, 9, animeId: 55, service: "mal", malAnimeId: 55);
        Signal(550, AnimeWatchStatus.Completed, 5, animeId: 102, service: "anilist", malAnimeId: 56);

        using var db = _fixture.NewContext(1);
        var snapshot = await Service().SnapshotAsync(db, new TestCurrentUser(1));

        // (9 + 5) / 2, not (9 + 9 + 5) / 3.
        Assert.Equal(0.7 * AnimeSignalPolicy.SeedScaleOf(AnimeSignalPolicy.DefaultStrength), snapshot.Effective.Weights[550], 8);
        Assert.Single(snapshot.Effective.EligibleIds, 550L);
    }

    /// <summary>
    /// Seasons of one franchise are one opinion about one manga, and the weakest one no longer gets
    /// to be outvoted by the strongest: before grouping the highest season's weight simply won.
    /// </summary>
    [Fact]
    public async Task Seasons_of_one_franchise_seed_at_their_average()
    {
        OptIn();
        Signal(560, AnimeWatchStatus.Completed, 10, animeId: 1);
        Signal(560, AnimeWatchStatus.Completed, 8, animeId: 2);

        using var db = _fixture.NewContext(1);
        var snapshot = await Service().SnapshotAsync(db, new TestCurrentUser(1));

        Assert.Equal(0.9 * AnimeSignalPolicy.SeedScaleOf(AnimeSignalPolicy.DefaultStrength), snapshot.Effective.Weights[560], 8);
    }

    /// <summary>
    /// Averaging has to be able to take a title out of the seeds entirely, or it is only cosmetic.
    /// One loved season and one disliked one is a lukewarm opinion about the manga behind both.
    /// </summary>
    [Fact]
    public async Task A_franchise_the_reader_cooled_on_seeds_nothing_either_way()
    {
        OptIn();
        Signal(570, AnimeWatchStatus.Completed, 9, animeId: 1);
        Signal(570, AnimeWatchStatus.Completed, 4, animeId: 2);

        using var db = _fixture.NewContext(1);
        var snapshot = await Service().SnapshotAsync(db, new TestCurrentUser(1));

        Assert.DoesNotContain(570L, snapshot.Effective.EligibleIds);
        Assert.DoesNotContain(570L, snapshot.Avoided.Keys);
    }

    /// <summary>
    /// The dial, end to end. Each level is a share of what rating the manga would carry, and at Full
    /// a 9/10 anime seeds at exactly the 1.8 a 9 on the shelf does.
    /// </summary>
    [Theory]
    [InlineData(AnimeSignalStrength.Subtle, 0.63)]
    [InlineData(AnimeSignalStrength.Balanced, 0.9)]
    [InlineData(AnimeSignalStrength.Full, 1.8)]
    public async Task The_strength_dial_scales_what_a_loved_anime_seeds_at(
        AnimeSignalStrength level, double expected)
    {
        OptIn();
        SetStrength(level);
        Signal(580, AnimeWatchStatus.Completed, 9);

        using var db = _fixture.NewContext(1);
        var snapshot = await Service().SnapshotAsync(db, new TestCurrentUser(1));

        Assert.Equal(expected, snapshot.Effective.Weights[580], 8);
    }

    /// <summary>
    /// The half the dial exists for. A bad anime score is usually a complaint about the adaptation,
    /// so the default must push it well under the 1.0 a deliberate thumbs down carries.
    /// </summary>
    [Theory]
    [InlineData(AnimeSignalStrength.Subtle, 0.35)]
    [InlineData(AnimeSignalStrength.Balanced, 0.5)]
    [InlineData(AnimeSignalStrength.Full, 1.0)]
    public async Task The_strength_dial_scales_a_complaint_too(
        AnimeSignalStrength level, double expected)
    {
        OptIn();
        SetStrength(level);
        Signal(590, AnimeWatchStatus.Completed, 1);

        using var db = _fixture.NewContext(1);
        var snapshot = await Service().SnapshotAsync(db, new TestCurrentUser(1));

        Assert.Equal(expected, snapshot.Avoided[590], 8);
        Assert.DoesNotContain(590L, snapshot.Effective.EligibleIds);
    }

    /// <summary>
    /// The pool is cached for twelve hours on this fingerprint, so a dial nobody can see the effect
    /// of until tomorrow is a dial that looks broken.
    /// </summary>
    [Fact]
    public async Task Moving_the_dial_changes_the_fingerprint()
    {
        OptIn();
        Signal(595, AnimeWatchStatus.Completed, 9);

        async Task<string> FingerprintAsync()
        {
            using var db = _fixture.NewContext(1);
            return (await Service().SnapshotAsync(db, new TestCurrentUser(1))).Fingerprint();
        }

        var atDefault = await FingerprintAsync();
        SetStrength(AnimeSignalStrength.Full);
        Assert.NotEqual(atDefault, await FingerprintAsync());
    }

    [Fact]
    public async Task Planning_and_a_lukewarm_score_reach_nothing()
    {
        OptIn();
        Signal(700, AnimeWatchStatus.Planning, 10, animeId: 1);
        Signal(701, AnimeWatchStatus.Completed, 6, animeId: 2);

        using var db = _fixture.NewContext(1);
        var snapshot = await Service().SnapshotAsync(db, new TestCurrentUser(1));

        Assert.DoesNotContain(700L, snapshot.Effective.EligibleIds);
        Assert.DoesNotContain(701L, snapshot.Effective.EligibleIds);
        Assert.Empty(snapshot.Avoided);
    }

    [Fact]
    public async Task Nothing_loads_until_the_reader_opts_in()
    {
        Signal(800, AnimeWatchStatus.Completed, 10);

        using var db = _fixture.NewContext(1);
        var snapshot = await Service().SnapshotAsync(db, new TestCurrentUser(1));

        Assert.DoesNotContain(800L, snapshot.Effective.EligibleIds);
        Assert.Empty(snapshot.Effective.Weights);
    }

    [Fact]
    public async Task The_instance_switch_overrides_the_opt_in()
    {
        OptIn();
        Signal(810, AnimeWatchStatus.Completed, 10);
        var settings = new FakeAppSettings().Set(SettingKeys.RecommendationsAnimeSignals, "false");

        using var db = _fixture.NewContext(1);
        var snapshot = await Service(settings).SnapshotAsync(db, new TestCurrentUser(1));

        Assert.DoesNotContain(810L, snapshot.Effective.EligibleIds);
    }

    /// <summary>
    /// The reader read the manga and rated it 2. The adaptation being excellent does not overturn
    /// that: library evidence is about the book, the anime signal is about something else.
    /// </summary>
    [Fact]
    public async Task A_library_row_wins_over_the_anime_it_was_adapted_into()
    {
        var seriesId = _fixture.SeedSeries("Owned", configure: s => s.MangaBakaId = 900);
        OptIn();
        Signal(900, AnimeWatchStatus.Completed, 10);

        using (var db = _fixture.NewContext(1))
        {
            db.UserSeriesStates.Add(new UserSeriesState { UserId = 1, SeriesId = seriesId, Rating = 2 });
            await db.SaveChangesAsync();
        }

        using var read = _fixture.NewContext(1);
        var snapshot = await Service().SnapshotAsync(read, new TestCurrentUser(1));

        Assert.DoesNotContain(900L, snapshot.Effective.EligibleIds);
        Assert.False(snapshot.Effective.Weights.ContainsKey(900));
        Assert.Equal(RecommendationFeedbackPolicy.AvoidStrength(2), snapshot.Avoided[900], 8);
    }

    [Fact]
    public async Task An_explicit_thumbs_down_wins_over_a_loved_adaptation()
    {
        OptIn();
        Signal(910, AnimeWatchStatus.Completed, 10);

        using (var db = _fixture.NewContext(1))
        {
            db.RecommendationFeedback.Add(new RecommendationFeedback
            { UserId = 1, ProviderId = 910, Sentiment = RecommendationSentiment.Disliked, Revision = 1 });
            await db.SaveChangesAsync();
        }

        using var read = _fixture.NewContext(1);
        var snapshot = await Service().SnapshotAsync(read, new TestCurrentUser(1));

        Assert.DoesNotContain(910L, snapshot.Effective.EligibleIds);
        Assert.Equal(1.0, snapshot.Avoided[910], 8);
    }

    [Fact]
    public async Task Ignoring_a_title_as_a_seed_also_silences_its_adaptation()
    {
        OptIn();
        Signal(920, AnimeWatchStatus.Completed, 10, animeId: 1);
        Signal(921, AnimeWatchStatus.Dropped, null, animeId: 2);

        using (var db = _fixture.NewContext(1))
        {
            db.RecommendationSignalOverrides.Add(new RecommendationSignalOverride
            { UserId = 1, ProviderId = 920, IgnoreAsSeed = true });
            db.RecommendationSignalOverrides.Add(new RecommendationSignalOverride
            { UserId = 1, ProviderId = 921, IgnoreAsSeed = true });
            await db.SaveChangesAsync();
        }

        using var read = _fixture.NewContext(1);
        var snapshot = await Service().SnapshotAsync(read, new TestCurrentUser(1));

        Assert.DoesNotContain(920L, snapshot.Effective.EligibleIds);
        Assert.Empty(snapshot.Avoided);
    }

    /// <summary>
    /// The pool cache is keyed on the fingerprint, so a list that changed has to produce a different
    /// one or a new signal sits behind a twelve-hour hit.
    /// </summary>
    [Fact]
    public async Task The_fingerprint_moves_when_a_signal_appears_and_when_its_score_changes()
    {
        OptIn();
        using var db = _fixture.NewContext(1);
        var service = Service();
        var before = (await service.SnapshotAsync(db, new TestCurrentUser(1))).Fingerprint();

        Signal(930, AnimeWatchStatus.Completed, 9);
        using var afterDb = _fixture.NewContext(1);
        var added = (await service.SnapshotAsync(afterDb, new TestCurrentUser(1))).Fingerprint();
        Assert.NotEqual(before, added);

        using (var edit = _fixture.NewContext(1))
        {
            var row = edit.AnimeSignals.Single();
            row.Score = 2;
            await edit.SaveChangesAsync();
        }

        using var rescoredDb = _fixture.NewContext(1);
        var rescored = (await service.SnapshotAsync(rescoredDb, new TestCurrentUser(1))).Fingerprint();
        Assert.NotEqual(added, rescored);
        Assert.NotEqual(before, rescored);
    }
}
