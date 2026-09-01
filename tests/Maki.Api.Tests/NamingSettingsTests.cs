using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Naming;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The library settings section's half of naming: formats validate before they're stored, and a
/// caller that leaves them out of the payload doesn't wipe them. The second one is not theoretical
/// — the setup wizard PUTs this section with two of its fields filled in.
/// </summary>
public class NamingSettingsTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly SettingsService _settings;

    public NamingSettingsTests() => _settings = new SettingsService(_db.ScopeFactory());

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// Only the settings store and the naming service are exercised by the library endpoints; the
    /// rest of this controller's dependencies belong to sections these tests never call.
    /// </summary>
    private SettingsController Controller() => new(
        settings: _settings,
        naming: new NamingService(_settings),
        flareSolverr: null!, prowlarr: null!, qbittorrent: null!, kavita: null!, configFile: null!,
        sourceRegistry: null!, sourceAvailability: null!, mangaBakaDump: null!, embeddingModel: null!,
        embeddingStore: null!, embeddingStatus: null!, embeddingIndexer: null!, embeddingOptions: null!,
        prebuiltIndex: null!, recoGraph: null!, recoGraphCache: null!, coReadInstaller: null!,
        coReadCache: null!, tasteVectorInstaller: null!, vectorIndexCache: null!, modelSwitcher: null!,
        db: _db.NewContext(), updateCheck: null!, currentUser: null!, userSettings: null!,
        kavitaUser: null!, schedulerFactory: null!, scopeFactory: _db.ScopeFactory(),
        logger: NullLogger<SettingsController>.Instance);

    private static SettingsController.LibrarySettings Payload(
        string? seriesFolderFormat = null, string? chapterFormat = null) =>
        new(WriteComicInfo: true,
            FolderNamingMode: FolderNamingMode.Rename,
            SeriesFolderFormat: seriesFolderFormat,
            ChapterFormat: chapterFormat);

    private static SettingsController.LibrarySettings Body(IActionResult result) =>
        Assert.IsType<SettingsController.LibrarySettings>(Assert.IsType<OkObjectResult>(result).Value);

    [Fact]
    public async Task Unset_formats_read_as_the_defaults()
    {
        var settings = Body(await Controller().GetLibrary(CancellationToken.None));

        Assert.Equal(NamingDefaults.SeriesFolderFormat, settings.SeriesFolderFormat);
        Assert.Equal(NamingDefaults.ChapterFormat, settings.ChapterFormat);
    }

    [Fact]
    public async Task Formats_round_trip()
    {
        await Controller().SetLibrary(
            Payload("{Series Title} [{MalId}]", "{Series Title} {Chapter Number:000}"),
            CancellationToken.None);

        var settings = Body(await Controller().GetLibrary(CancellationToken.None));
        Assert.Equal("{Series Title} [{MalId}]", settings.SeriesFolderFormat);
        Assert.Equal("{Series Title} {Chapter Number:000}", settings.ChapterFormat);
    }

    [Theory]
    [InlineData("{Nonsense}", null)]
    [InlineData("Literal only", null)]
    [InlineData("{Series Title}/{Series Year}", null)]
    [InlineData(null, "{Chapter Number:abc}")]
    [InlineData(null, "")]
    public async Task Invalid_formats_are_refused(string? seriesFolderFormat, string? chapterFormat)
    {
        var result = await Controller().SetLibrary(
            Payload(seriesFolderFormat, chapterFormat), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Null(await _settings.GetAsync(SettingKeys.LibrarySeriesFolderFormat));
        Assert.Null(await _settings.GetAsync(SettingKeys.LibraryChapterFormat));
    }

    [Fact]
    public async Task A_payload_without_the_formats_leaves_them_alone()
    {
        await Controller().SetLibrary(
            Payload("{Series Title} ({Series Year})", "{Series Title} {Chapter Number}"),
            CancellationToken.None);

        // What the setup wizard sends: the two switches it knows about, nothing else.
        var result = await Controller().SetLibrary(Payload(), CancellationToken.None);

        var settings = Body(result);
        Assert.Equal("{Series Title} ({Series Year})", settings.SeriesFolderFormat);
        Assert.Equal("{Series Title} {Chapter Number}", settings.ChapterFormat);
    }

    [Fact]
    public async Task Preview_renders_both_formats_against_the_sample()
    {
        var result = await Controller().PreviewNaming(
            new SettingsController.NamingPreviewRequest(
                "{Series Title} ({Series Year})", "{Series Title} {Chapter Number:000}"),
            CancellationToken.None);

        var preview = Assert.IsType<SettingsController.NamingPreviewResponse>(
            Assert.IsType<OkObjectResult>(result).Value);

        Assert.Equal("The Series Title's! (2010)", preview.SeriesFolder);
        Assert.Equal("The Series Title's! 024.cbz", preview.ChapterFile);
        Assert.Empty(preview.Errors);
    }

    [Fact]
    public async Task Preview_reports_what_a_save_would_refuse()
    {
        var result = await Controller().PreviewNaming(
            new SettingsController.NamingPreviewRequest("{Nope}", null), CancellationToken.None);

        var preview = Assert.IsType<SettingsController.NamingPreviewResponse>(
            Assert.IsType<OkObjectResult>(result).Value);

        Assert.Contains(preview.Errors, e => e.StartsWith("Series folder format:"));
    }

    [Fact]
    public void Every_token_comes_back_with_an_example()
    {
        var result = Assert.IsType<OkObjectResult>(Controller().GetNamingTokens());
        var tokens = Assert.IsAssignableFrom<IEnumerable<SettingsController.NamingTokenDto>>(result.Value)
            .ToList();

        Assert.Equal(NamingTokens.All.Count, tokens.Count);
        Assert.All(tokens, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
        Assert.Contains(tokens, t => t is { Token: "{Series Title}", Example: "The Series Title's!" });
        Assert.Contains(tokens, t => t.Category == NamingTokenCategory.SeriesId);
    }

    [Fact]
    public async Task A_stored_format_that_no_longer_validates_falls_back_to_the_default()
    {
        // Only reachable by hand-editing the database or by a token being removed in an upgrade —
        // either way, junk names are worse than the default.
        await _settings.SetAsync(SettingKeys.LibraryChapterFormat, "{Gone Away}");

        Assert.Equal(NamingDefaults.ChapterFormat,
            await new NamingService(_settings).ChapterFormatAsync());
    }
}
