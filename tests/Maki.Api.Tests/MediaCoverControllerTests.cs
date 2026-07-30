using Maki.Api.Configuration;
using Maki.Api.Controllers;
using Maki.Core.Security;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Tests;

/// <summary>
/// Cover art is library data, and the endpoint that serves it derives a filesystem path from a
/// caller-supplied id. Serving that file without resolving the series first would put every cover in
/// the instance one URL away from any account — including one granted a single root folder — and no
/// other test would notice, because the leak is in what the controller *doesn't* do.
/// </summary>
public class MediaCoverControllerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _configDir;
    private readonly string? _priorEnv;
    private readonly AppPaths _paths;

    public MediaCoverControllerTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "maki-mediacover-tests", Guid.NewGuid().ToString("N"));
        _priorEnv = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _configDir);
        _paths = new AppPaths();
    }

    [Fact]
    public async Task ServesACoverForASeriesInAGrantedRootFolder()
    {
        var (granted, _, reader) = Seed();

        var result = await Controller(reader).Cover(granted, default);

        Assert.IsType<PhysicalFileResult>(result);
    }

    [Fact]
    public async Task RefusesACoverForASeriesInAFolderTheUserWasNotGranted()
    {
        var (_, ungranted, reader) = Seed();

        var result = await Controller(reader).Cover(ungranted, default);

        // 404 and not 403: a series the caller cannot see must not be confirmed to exist.
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task ServesEveryCoverToAUserWithAllRootFolders()
    {
        var (granted, ungranted, _) = Seed();
        var admin = _db.SeedUser("admin", MakiPermission.Admin, allRootFolders: true);

        Assert.IsType<PhysicalFileResult>(await Controller(admin, allRootFolders: true).Cover(granted, default));
        Assert.IsType<PhysicalFileResult>(await Controller(admin, allRootFolders: true).Cover(ungranted, default));
    }

    /// <summary>
    /// Two series in two root folders, a cover file on disk for each, and a reader granted only the
    /// first. <c>SeedSeries</c> creates a root folder per call, so the two are genuinely separate.
    /// </summary>
    private (int Granted, int Ungranted, int Reader) Seed()
    {
        var granted = _db.SeedSeries("Granted");
        var ungranted = _db.SeedSeries("Ungranted");
        var reader = _db.SeedUser("reader", MakiPermission.None, allRootFolders: false);

        using var db = _db.NewContext();
        var grantedFolder = db.Series.Single(s => s.Id == granted).RootFolderId;
        db.UserRootFolders.Add(new UserRootFolder { UserId = reader, RootFolderId = grantedFolder });
        db.SaveChanges();

        WriteCover(granted);
        WriteCover(ungranted);

        return (granted, ungranted, reader);
    }

    private void WriteCover(int seriesId)
    {
        var dir = Path.Combine(_paths.MediaCoverDir, seriesId.ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "cover.jpg"), [0xFF, 0xD8, 0xFF]);
    }

    private MediaCoverController Controller(int userId, bool allRootFolders = false) =>
        new(_paths, _db.NewContext(userId, allRootFolders));

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _priorEnv);
        try { Directory.Delete(_configDir, recursive: true); } catch (IOException) { }
    }
}
