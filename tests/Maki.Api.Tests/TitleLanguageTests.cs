using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The per-user title language: what it changes (<see cref="SeriesDto.DisplayTitle"/>) and, more
/// importantly, what it must never change (<see cref="Series.Title"/> and <c>SortTitle</c>, which
/// name the folder on disk and every file in it).
/// </summary>
public sealed class TitleLanguageTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static Series Berserk() => new()
    {
        Id = 1,
        Title = "Berserk",
        SortTitle = "berserk",
        OriginalTitle = "ベルセルク",
        AltTitles =
        [
            new LocalizedTitle("Kentaro Miura's Berserk", null),
            new LocalizedTitle("Berserk: La Edición Definitiva", "es"),
            new LocalizedTitle("Kenrou Densetsu Berserk", "ja-latn"),
        ],
        FolderName = "Berserk (1989)",
    };

    [Theory]
    // No preference at all: the canonical title, which is what every build before this did.
    [InlineData(null, "Berserk")]
    [InlineData("", "Berserk")]
    [InlineData("es", "Berserk: La Edición Definitiva")]
    // Nothing tagged French, and the "en" fallback the settings UI appends resolves to the
    // canonical title rather than to whatever the provider happened to list first.
    [InlineData("fr,en", "Berserk")]
    // "native" is the pseudo-code for OriginalTitle, which carries no language tag of its own.
    [InlineData("native", "ベルセルク")]
    // A script variant is a different script: asking for Japanese must not hand back a romanization.
    [InlineData("ja", "Berserk")]
    [InlineData("ja-latn", "Kenrou Densetsu Berserk")]
    public void Display_title_follows_the_preference(string? preference, string expected)
    {
        var dto = SeriesDto.FromEntity(Berserk(), titleLanguage: preference);

        Assert.Equal(expected, dto.DisplayTitle);
    }

    [Fact]
    public void The_canonical_title_and_sort_key_are_untouched_by_a_preference()
    {
        // The whole reason this is resolved at DTO-build time: Series.Title is what the folder on
        // disk and the file names are derived from, so one person's preference must not rename
        // another person's library.
        var dto = SeriesDto.FromEntity(Berserk(), titleLanguage: "es");

        Assert.Equal("Berserk: La Edición Definitiva", dto.DisplayTitle);
        Assert.Equal("Berserk", dto.Title);
        Assert.Equal("berserk", dto.SortTitle);
        Assert.Equal("Berserk (1989)", dto.FolderName);
    }

    [Fact]
    public void Alt_titles_reach_the_wire_with_their_languages()
    {
        var dto = SeriesDto.FromEntity(Berserk());

        Assert.Equal(
            [
                new LocalizedTitleDto("Kentaro Miura's Berserk", null),
                new LocalizedTitleDto("Berserk: La Edición Definitiva", "es"),
                new LocalizedTitleDto("Kenrou Densetsu Berserk", "ja-latn"),
            ],
            dto.AltTitles);
    }

    private SettingsController Controller(int userId)
    {
        var db = _db.NewContext();
        return new SettingsController(
            localizer: new TestLocalizer(), userLocales: new TestUserLocaleResolver(),
            settings: null!, naming: null!, flareSolverr: null!, prowlarr: null!, qbittorrent: null!,
            kavita: null!, sourceRegistry: null!, sourceAvailability: null!,
            mangaBakaDump: null!, embeddingModel: null!, embeddingStore: null!, embeddingStatus: null!,
            embeddingIndexer: null!, embeddingOptions: null!, prebuiltIndex: null!, recoGraph: null!,
            recoGraphCache: null!, coReadInstaller: null!, coReadCache: null!, readerCohortInstaller: null!,
            readerCohortCache: null!, tasteVectorInstaller: null!, vectorIndexCache: null!,
            modelSwitcher: null!, db: db, updateCheck: null!, currentUser: new TestCurrentUser(userId),
            userSettings: new UserSettingsService(db, new TestCurrentUser(userId)),
            kavitaUser: null!, schedulerFactory: null!, scopeFactory: _db.ScopeFactory(),
            logger: NullLogger<SettingsController>.Instance);
    }

    private static SettingsController.UiSettings Body(IActionResult result) =>
        Assert.IsType<SettingsController.UiSettings>(Assert.IsType<OkObjectResult>(result).Value);

    [Fact]
    public async Task The_preference_round_trips_through_the_ui_settings_endpoint()
    {
        var alice = _db.SeedUser("alice");

        var written = Body(await Controller(alice).SetUi(
            new SettingsController.UiSettings("library", HomeLayoutSpec.Default, null, " JA , en "),
            CancellationToken.None));

        // Normalized on the way in, so the stored value can be compared for change without every
        // reader having to re-normalize it.
        Assert.Equal("ja,en", written.TitleLanguage);
        Assert.Equal("ja,en", Body(await Controller(alice).GetUi(CancellationToken.None)).TitleLanguage);
    }

    [Fact]
    public async Task Clearing_the_preference_deletes_the_row_rather_than_storing_a_default()
    {
        var alice = _db.SeedUser("alice");
        await Controller(alice).SetUi(
            new SettingsController.UiSettings("library", HomeLayoutSpec.Default, null, "ja"),
            CancellationToken.None);

        await Controller(alice).SetUi(
            new SettingsController.UiSettings("library", HomeLayoutSpec.Default, null, ""),
            CancellationToken.None);

        using var db = _db.NewContext();
        Assert.DoesNotContain(
            db.UserSettings.ToList(),
            row => row.Key == SettingKeys.UiTitleLanguage);
    }

    [Fact]
    public async Task One_users_preference_is_not_another_users()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");

        await Controller(alice).SetUi(
            new SettingsController.UiSettings("library", HomeLayoutSpec.Default, null, "ja"),
            CancellationToken.None);

        Assert.Null(Body(await Controller(bob).GetUi(CancellationToken.None)).TitleLanguage);
    }
}
