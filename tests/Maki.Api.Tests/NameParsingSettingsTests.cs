using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Parsing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public class NameParsingProfileCollection
{
    /// <summary>Tests that swap <see cref="NameParsingProfile.Current"/>, which every parse in the process reads.</summary>
    public const string Name = "NameParsingProfile";
}

[Collection(NameParsingProfileCollection.Name)]
public class NameParsingSettingsTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly SettingsService _settings;

    public NameParsingSettingsTests() => _settings = new SettingsService(_db.ScopeFactory());

    public void Dispose()
    {
        NameParsingProfile.Current = NameParsingProfile.Default;
        _db.Dispose();
    }

    private SettingsController Controller() => new(
        localizer: new TestLocalizer(), userLocales: new TestUserLocaleResolver(),
        settings: _settings,
        naming: new NamingService(_settings),
        flareSolverr: null!, prowlarr: null!, qbittorrent: null!, kavita: null!,
        sourceRegistry: null!, sourceAvailability: null!, mangaBakaDump: null!, embeddingModel: null!,
        embeddingStore: null!, embeddingStatus: null!, embeddingIndexer: null!,
        prebuiltIndex: null!, recoGraph: null!, recoGraphCache: null!, coReadInstaller: null!,
        coReadCache: null!, readerCohortInstaller: null!, readerCohortCache: null!,
        tasteVectorInstaller: null!, vectorIndexCache: null!, modelSwitcher: null!,
        db: _db.NewContext(), updateCheck: null!, currentUser: null!, userSettings: null!,
        kavitaUser: null!, kavitaLive: null!, schedulerFactory: null!, scopeFactory: _db.ScopeFactory(),
        logger: NullLogger<SettingsController>.Instance);

    private static SettingsController.NameParsingSettingsDto Body(IActionResult result) =>
        Assert.IsType<SettingsController.NameParsingSettingsDto>(Assert.IsType<OkObjectResult>(result).Value);

    [Fact]
    public async Task Saving_languages_stores_them_and_switches_the_parsers_at_once()
    {
        var saved = Body(await Controller().SetNameParsing(new(["ja", "fr"]), CancellationToken.None));

        Assert.Equal(["fr", "ja"], saved.Languages);
        Assert.Equal("fr,ja", await _settings.GetAsync(SettingKeys.ParsingLanguages));
        Assert.Equal(7, ReleaseNameParser.ParseFileName("Series_Name_Tome_7.cbz").Volume);
        Assert.Equal(10, saved.Available.Count);
    }

    [Fact]
    public async Task Switching_every_language_off_parses_english_only_again()
    {
        await Controller().SetNameParsing(new(["fr"]), CancellationToken.None);
        await Controller().SetNameParsing(new([]), CancellationToken.None);

        Assert.Null(await _settings.GetAsync(SettingKeys.ParsingLanguages));
        Assert.Equal(7m, ReleaseNameParser.ParseFileName("Series_Name_Tome_7.cbz").Number);
    }

    [Theory]
    [InlineData("xx")]
    [InlineData("en")]
    public async Task A_language_without_marker_words_is_refused(string language)
    {
        var result = await Controller().SetNameParsing(new([language]), CancellationToken.None);

        Assert.Equal(400, Assert.IsAssignableFrom<ObjectResult>(result).StatusCode);
        Assert.Same(NameParsingProfile.Default, NameParsingProfile.Current);
    }
}
