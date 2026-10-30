using Maki.Api.Configuration;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using VipsImage = NetVips.Image;

namespace Maki.Api.Tests;

/// <summary>
/// The sign-in page's cover mosaic is the one anonymous view of the library, so what it draws is
/// pinned here: nothing while the setting is off, only Safe-rated series, and a blurred image.
/// </summary>
public class SignInMosaicServiceTests : IDisposable
{
    private readonly string _configDir =
        Path.Combine(Path.GetTempPath(), "maki-signin-mosaic-" + Guid.NewGuid().ToString("N"));
    private readonly TestDb _db = new();
    private readonly FakeAppSettings _settings = new();
    private readonly CoverService _covers;
    private readonly SignInMosaicService _mosaic;
    private int _rootFolderId;

    public SignInMosaicServiceTests()
    {
        var paths = new AppPaths(_configDir);
        _covers = new CoverService(null!, paths, _settings, NullLogger<CoverService>.Instance);
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.NewContext());
        _mosaic = new SignInMosaicService(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            _settings, _covers, NullLogger<SignInMosaicService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
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

    private int AddFolderSeries(string name, int count)
    {
        int folderId;
        using (var db = _db.NewContext())
        {
            var root = new RootFolder { Path = Path.Combine(_configDir, name) };
            db.RootFolders.Add(root);
            db.SaveChanges();
            folderId = root.Id;
        }

        var open = _rootFolderId;
        _rootFolderId = folderId;
        AddSeries(count, "safe");
        _rootFolderId = open;
        return folderId;
    }

    private void AddUser(string name, bool allFolders, params int[] grantedFolders)
    {
        using var db = _db.NewContext();
        var user = new MakiUser
        {
            UserName = name,
            NormalizedUserName = name.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            CreatedAt = DateTime.UtcNow,
            AllRootFolders = allFolders,
        };
        db.Users.Add(user);
        db.SaveChanges();
        foreach (var folder in grantedFolders)
        {
            db.UserRootFolders.Add(new UserRootFolder { UserId = user.Id, RootFolderId = folder });
        }

        db.SaveChanges();
    }

    private int FirstSeriesId()
    {
        using var db = _db.NewContext();
        return db.Series.IgnoreQueryFilters().Min(s => s.Id);
    }

    private IEnumerable<string> CoverFiles()
    {
        using var db = _db.NewContext();
        return db.Series.IgnoreQueryFilters().OrderBy(s => s.Id).Select(s => s.Id).ToList()
            .Select(_covers.CoverPathFor).ToList();
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

    [Fact]
    public async Task A_folder_a_restricted_user_has_no_grant_for_is_never_picked()
    {
        var folderA = AddFolderSeries("a", 3);
        var folderB = AddFolderSeries("b", 4);
        AddUser("everything", allFolders: true);
        AddUser("restricted", allFolders: false, folderA);

        await using var db = _db.NewContext();
        var picked = await SignInMosaicService.PickAsync(db, CancellationToken.None);

        Assert.Equal(3, picked.Count);
        Assert.All(picked, id => Assert.Equal(folderA, db.Series.IgnoreQueryFilters().Single(s => s.Id == id).RootFolderId));
        Assert.DoesNotContain(picked, id => db.Series.IgnoreQueryFilters().Single(s => s.Id == id).RootFolderId == folderB);
    }

    [Fact]
    public async Task A_restricted_user_with_no_grants_hides_every_folder()
    {
        AddFolderSeries("a", 3);
        AddUser("empty", allFolders: false);

        await using var db = _db.NewContext();

        Assert.Empty(await SignInMosaicService.PickAsync(db, CancellationToken.None));
    }

    [Fact]
    public async Task With_no_restricted_users_every_folder_is_picked()
    {
        AddFolderSeries("a", 3);
        AddFolderSeries("b", 4);
        AddUser("everything", allFolders: true);

        await using var db = _db.NewContext();

        Assert.Equal(7, (await SignInMosaicService.PickAsync(db, CancellationToken.None)).Count);
    }

    [Fact]
    public void A_cover_with_a_good_header_and_a_broken_body_is_left_out_not_fatal()
    {
        AddSeries(SignInMosaicService.MinCovers + 1, "safe");
        var files = CoverFiles().ToList();
        using (var noise = VipsImage.Gaussnoise(200, 300))
        using (var rgb = noise.Bandjoin([noise, noise]))
        {
            rgb.Pngsave(files[0]);
        }

        var bytes = File.ReadAllBytes(files[0]);
        File.WriteAllBytes(files[0], bytes[..(bytes.Length / 2)]);

        var webp = SignInMosaicService.Render(files);

        Assert.NotNull(webp);
    }

    [Fact]
    public async Task A_corrupt_cover_is_left_out_and_the_rest_still_draw()
    {
        _settings.Set(SettingKeys.SignInCoverMosaic, "true");
        AddSeries(SignInMosaicService.MinCovers + 2, "safe");
        File.WriteAllText(CoverFiles().First(), "not an image");

        var mosaic = await _mosaic.GetAsync(CancellationToken.None);

        Assert.NotNull(mosaic);
        using var image = VipsImage.NewFromBuffer(mosaic.Webp);
        Assert.Equal(4, image.Bands);
    }

    [Fact]
    public async Task A_failed_render_is_not_retried_until_the_covers_change()
    {
        _settings.Set(SettingKeys.SignInCoverMosaic, "true");
        AddSeries(SignInMosaicService.MinCovers, "safe");
        var files = CoverFiles().ToList();
        File.WriteAllText(files[0], "not an image");

        Assert.Null(await _mosaic.GetAsync(CancellationToken.None));

        // Fixed on disk with the same timestamp: the inputs look unchanged, so it is not tried again.
        var stamp = File.GetLastWriteTimeUtc(files[0]);
        WriteCover(FirstSeriesId());
        File.SetLastWriteTimeUtc(files[0], stamp);
        Assert.Null(await _mosaic.GetAsync(CancellationToken.None));

        File.SetLastWriteTimeUtc(files[0], stamp.AddMinutes(1));
        Assert.NotNull(await _mosaic.GetAsync(CancellationToken.None));
    }
}
