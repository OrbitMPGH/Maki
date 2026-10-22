using Maki.Api.Configuration;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VipsImage = NetVips.Image;

namespace Maki.Api.Tests;

/// <summary>
/// The sign-in page's cover mosaic is the one anonymous view of the library, so what it draws is
/// pinned here: nothing while the setting is off, only Safe-rated series, and a blurred image.
/// </summary>
[Collection(ConfigDirCollection.Name)]
public class SignInMosaicServiceTests : IDisposable
{
    private readonly string _configDir =
        Path.Combine(Path.GetTempPath(), "maki-signin-mosaic-" + Guid.NewGuid().ToString("N"));
    private readonly string? _priorEnv = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
    private readonly TestDb _db = new();
    private readonly FakeAppSettings _settings = new();
    private readonly CoverService _covers;
    private readonly SignInMosaicService _mosaic;
    private int _rootFolderId;

    public SignInMosaicServiceTests()
    {
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _configDir);
        var paths = new AppPaths();
        _covers = new CoverService(null!, paths, _settings, NullLogger<CoverService>.Instance);
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.NewContext());
        _mosaic = new SignInMosaicService(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            _settings, _covers, NullLogger<SignInMosaicService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _priorEnv);
        try
        {
            Directory.Delete(_configDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private void AddSeries(int count, string? rating, bool withCover = true)
    {
        using var db = _db.NewContext();
        if (_rootFolderId == 0)
        {
            var root = new RootFolder { Path = Path.Combine(_configDir, "library") };
            db.RootFolders.Add(root);
            db.SaveChanges();
            _rootFolderId = root.Id;
        }

        for (var i = 0; i < count; i++)
        {
            var title = $"{rating ?? "unrated"} {Guid.NewGuid():N}";
            var series = new Series
            {
                Title = title,
                SortTitle = title,
                FolderName = title,
                RootFolderId = _rootFolderId,
                ContentRating = rating,
                CoverPath = withCover ? "cover.jpg" : null,
                Added = DateTime.UtcNow,
            };
            db.Series.Add(series);
            db.SaveChanges();
            if (withCover)
            {
                WriteCover(series.Id);
            }
        }
    }

    private void WriteCover(int seriesId)
    {
        var path = _covers.CoverPathFor(seriesId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var black = VipsImage.Black(200, 300, bands: 3);
        using var grey = black.Linear([1.0], [90.0 + seriesId % 100]);
        grey.Jpegsave(path);
    }

    [Fact]
    public async Task Nothing_is_drawn_while_the_setting_is_off()
    {
        AddSeries(12, "safe");

        Assert.Null(await _mosaic.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Only_safe_series_with_a_poster_are_picked()
    {
        AddSeries(3, "safe");
        AddSeries(2, "suggestive");
        AddSeries(2, "pornographic");
        AddSeries(2, null);
        AddSeries(2, "safe", withCover: false);

        await using var db = _db.NewContext();
        var picked = await SignInMosaicService.PickAsync(db, CancellationToken.None);

        Assert.Equal(3, picked.Count);
        Assert.All(picked, id => Assert.Equal("safe", db.Series.Find(id)!.ContentRating));
    }

    [Fact]
    public async Task Too_few_safe_covers_draws_nothing()
    {
        _settings.Set(SettingKeys.SignInCoverMosaic, "true");
        AddSeries(SignInMosaicService.MinCovers - 1, "safe");
        AddSeries(10, "suggestive");

        Assert.Null(await _mosaic.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task With_the_setting_on_it_draws_one_webp_and_reuses_it()
    {
        _settings.Set(SettingKeys.SignInCoverMosaic, "true");
        AddSeries(SignInMosaicService.MaxCovers + 4, "safe");

        var first = await _mosaic.GetAsync(CancellationToken.None);
        var second = await _mosaic.GetAsync(CancellationToken.None);

        Assert.NotNull(first);
        Assert.Same(first, second);
        using var image = VipsImage.NewFromBuffer(first.Webp);
        Assert.Equal(4, image.Bands);
        var columns = SignInMosaicService.Columns;
        Assert.Equal(columns * SignInMosaicService.TileWidth + (columns - 1) * SignInMosaicService.Gap, image.Width);
        Assert.Equal(3 * SignInMosaicService.TileHeight + 2 * SignInMosaicService.Gap, image.Height);
    }
}
