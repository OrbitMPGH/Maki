using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Reading;
using Maki.Data;
using Maki.Data.Identity;

namespace Maki.Api.Tests;

/// <summary>
/// The four-layer resolution order, which is the whole contract of reading profiles: series
/// override, pinned profile, the profile claiming the series' type, global defaults.
/// </summary>
public class ReadingProfileServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private const int UserId = 1;

    [Fact]
    public async Task AManhwaPicksTheWebtoonProfileWithNothingConfigured()
    {
        var seriesId = _db.SeedSeries("Solo Leveling", configure: s => s.Type = SeriesTypes.Manhwa);
        await SeedDefaultProfilesAsync();

        var resolved = await Service().ResolveAsync(seriesId, default);

        Assert.Equal(ReaderPrefsSource.Profile, resolved.Source);
        Assert.Equal("Webtoon", resolved.ProfileName);
        Assert.Equal(ReaderPrefsSpec.ModeVertical, resolved.Prefs.Mode);
        Assert.Equal(ReaderPrefsSpec.DirectionLtr, resolved.Prefs.Direction);
        Assert.Equal(ReaderPrefsSpec.FitOriginal, resolved.Prefs.Fit);

        // Auto-selected, not pinned: the picker has to be able to tell those apart.
        Assert.Null(resolved.PinnedProfileId);
        Assert.Equal(resolved.ProfileId, resolved.AutoProfileId);
    }

    [Fact]
    public async Task AMangaPicksTheMangaProfile()
    {
        var seriesId = _db.SeedSeries("Berserk", configure: s => s.Type = SeriesTypes.Manga);
        await SeedDefaultProfilesAsync();

        var resolved = await Service().ResolveAsync(seriesId, default);

        Assert.Equal("Manga", resolved.ProfileName);
        Assert.Equal(ReaderPrefsSpec.ModePaged, resolved.Prefs.Mode);
        Assert.Equal(ReaderPrefsSpec.DirectionRtl, resolved.Prefs.Direction);
        Assert.Equal(ReaderPrefsSpec.FitHeight, resolved.Prefs.Fit);
    }

    /// <summary>
    /// The upgrade path: every existing series lands with a null type and only gains one on the next
    /// metadata refresh. Until then the reader has to behave exactly as it did before profiles.
    /// </summary>
    [Fact]
    public async Task ASeriesWithNoTypeYetFallsBackToTheGlobalDefaults()
    {
        var seriesId = _db.SeedSeries("Never Refreshed");
        await SeedDefaultProfilesAsync();
        _db.SetUserConfig(UserId, (SettingKeys.ReaderPrefs,
            ReaderPrefsSpec.Serialize(new ReaderPrefsSpec(Fit: ReaderPrefsSpec.FitWidth))));

        var resolved = await Service().ResolveAsync(seriesId, default);

        Assert.Equal(ReaderPrefsSource.Global, resolved.Source);
        Assert.Equal(ReaderPrefsSpec.FitWidth, resolved.Prefs.Fit);
        Assert.Null(resolved.AutoProfileId);
    }

    [Fact]
    public async Task APinnedProfileBeatsTheOneTheTypeWouldPick()
    {
        var seriesId = _db.SeedSeries("Vertical-ish", configure: s => s.Type = SeriesTypes.Manhwa);
        await SeedDefaultProfilesAsync();
        var manga = ProfileNamed("Manga");
        var webtoon = ProfileNamed("Webtoon");

        await using (var db = _db.NewContext(UserId))
        {
            db.UserSeriesStates.Add(new UserSeriesState
            {
                UserId = UserId, SeriesId = seriesId, ReadingProfileId = manga.Id
            });
            await db.SaveChangesAsync();
        }

        var resolved = await Service().ResolveAsync(seriesId, default);

        Assert.Equal("Manga", resolved.ProfileName);
        Assert.Equal(manga.Id, resolved.PinnedProfileId);
        // Still reports what the type *would* have picked, so the picker can label its Auto entry.
        Assert.Equal(webtoon.Id, resolved.AutoProfileId);
    }

    [Fact]
    public async Task ASeriesOverrideBeatsEverything()
    {
        var seriesId = _db.SeedSeries("Odd One", configure: s => s.Type = SeriesTypes.Manhwa);
        await SeedDefaultProfilesAsync();

        await using (var db = _db.NewContext(UserId))
        {
            db.UserSeriesStates.Add(new UserSeriesState
            {
                UserId = UserId,
                SeriesId = seriesId,
                ReaderPrefsJson = ReaderPrefsSpec.Serialize(
                    new ReaderPrefsSpec(Mode: ReaderPrefsSpec.ModeDouble)),
            });
            await db.SaveChangesAsync();
        }

        var resolved = await Service().ResolveAsync(seriesId, default);

        Assert.Equal(ReaderPrefsSource.Series, resolved.Source);
        Assert.Equal(ReaderPrefsSpec.ModeDouble, resolved.Prefs.Mode);
        Assert.Null(resolved.ProfileId);
    }

    /// <summary>
    /// Profiles are per user, and so is the type claim. One person retuning their Webtoon profile
    /// must not reach anybody else's reader.
    /// </summary>
    [Fact]
    public async Task ProfilesDoNotLeakBetweenUsers()
    {
        var other = _db.SeedUser("other");
        var seriesId = _db.SeedSeries("Shared", configure: s => s.Type = SeriesTypes.Manhwa);
        await SeedDefaultProfilesAsync();

        var mine = await Service().ResolveAsync(seriesId, default);
        var theirs = await Service(other).ResolveAsync(seriesId, default);

        Assert.Equal(ReaderPrefsSource.Profile, mine.Source);
        Assert.Equal(ReaderPrefsSource.Global, theirs.Source);
    }

    [Fact]
    public async Task DeletingAProfileUnpinsTheSeriesInsteadOfLosingItsRating()
    {
        var seriesId = _db.SeedSeries("Pinned", configure: s => s.Type = SeriesTypes.Manga);
        await SeedDefaultProfilesAsync();
        var webtoon = ProfileNamed("Webtoon");

        await using (var db = _db.NewContext(UserId))
        {
            db.UserSeriesStates.Add(new UserSeriesState
            {
                UserId = UserId, SeriesId = seriesId, ReadingProfileId = webtoon.Id, Rating = 9
            });
            await db.SaveChangesAsync();
        }

        await using (var db = _db.NewContext(UserId))
        {
            db.ReadingProfiles.Remove(db.ReadingProfiles.Single(p => p.Id == webtoon.Id));
            await db.SaveChangesAsync();
        }

        await using var check = _db.NewContext(UserId);
        var state = check.UserSeriesStates.Single(s => s.SeriesId == seriesId);
        Assert.Null(state.ReadingProfileId);
        Assert.Equal(9, state.Rating);
    }

    [Fact]
    public async Task ASecondProfileClaimingTheSameTypeIsRefused()
    {
        await SeedDefaultProfilesAsync();

        var clash = await Service().ConflictingClaimAsync([SeriesTypes.Manhua], null, default);

        Assert.NotNull(clash);
        Assert.Equal(SeriesTypes.Manhua, clash!.Value.Type);
        Assert.Equal("Webtoon", clash.Value.ProfileName);
    }

    [Fact]
    public async Task AProfileMayKeepItsOwnClaimWhenEdited()
    {
        await SeedDefaultProfilesAsync();
        var webtoon = ProfileNamed("Webtoon");

        var clash = await Service().ConflictingClaimAsync(
            [SeriesTypes.Manhwa, SeriesTypes.Manhua], webtoon.Id, default);

        Assert.Null(clash);
    }

    private async Task SeedDefaultProfilesAsync()
    {
        await using var db = _db.NewContext(UserId);
        await ReadingProfileSeeder.SeedAsync(db, UserId, default);
    }

    private ReadingProfile ProfileNamed(string name)
    {
        using var db = _db.NewContext(UserId);
        return db.ReadingProfiles.Single(p => p.Name == name);
    }

    private ReadingProfileService Service(int userId = UserId)
    {
        var db = _db.NewContext(userId);
        return new ReadingProfileService(db, new UserSettingsService(db, new TestCurrentUser(userId)));
    }

    public void Dispose() => _db.Dispose();
}
