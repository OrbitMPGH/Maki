using Maki.Api.Configuration;
using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// A volume import moves the user's originals into <c>.maki-trash/&lt;SeriesId&gt;</c>, so deleting the
/// series but keeping its files must leave that folder alone.
/// </summary>
[Collection(ConfigDirCollection.Name)]
public class SeriesDeleteTrashTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "maki-delete-trash-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string? _previousConfigDir = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");

    public SeriesDeleteTrashTests()
    {
        _root = Path.Combine(_configDir, "library");
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _configDir);
    }

    public void Dispose()
    {
        _db.Dispose();
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _previousConfigDir);
        try
        {
            Directory.Delete(_configDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Only what <c>Delete</c> reaches for a series with no provider id.</summary>
    private SeriesController Controller(Maki.Data.MakiDbContext db) => new(
        localizer: new TestLocalizer(),
        db: db,
        coverService: new CoverService(null!, new AppPaths(), new FakeAppSettings(), NullLogger<CoverService>.Instance),
        chapterSyncService: null!,
        cbzLinkService: null!,
        relinkPlanner: null!,
        seriesCreation: null!,
        seriesRename: null!,
        metadataRefresh: null!,
        downloadQueue: null!,
        downloadBatches: null!,
        appSettings: null!,
        kavitaScans: null!,
        scrobbler: null!,
        stats: new StatsEventService(db),
        mangaBakaStore: null!,
        similarSeries: null!,
        recommendationFeedback: null!,
        archives: null!,
        readingProfiles: null!,
        readingTimeEstimates: null!,
        sourceAvailability: null!,
        currentUser: new TestCurrentUser(1),
        userSettings: null!,
        notifications: new RecordingNotifications(),
        locales: new TestUserLocaleResolver(),
        logger: NullLogger<SeriesController>.Instance);

    private (int SeriesId, string Trashed, string Kept) Seed()
    {
        using var db = _db.NewContext();
        var root = new RootFolder { Path = _root };
        db.RootFolders.Add(root);
        db.SaveChanges();
        var series = new Series { Title = "Berserk", SortTitle = "berserk", FolderName = "Berserk", RootFolderId = root.Id };
        db.Series.Add(series);
        db.SaveChanges();

        var kept = Path.Combine(_root, "Berserk", "Berserk v01.cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
        File.WriteAllText(kept, "cbz");
        db.ChapterFiles.Add(new ChapterFile
        {
            SeriesId = series.Id, RelativePath = Path.Combine("Berserk", "Berserk v01.cbz"), DateAdded = DateTime.UtcNow
        });
        db.SaveChanges();

        var trashed = Path.Combine(UpgradeTrash.SeriesFolder(_root, series.Id), "7-Berserk c001.cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(trashed)!);
        File.WriteAllText(trashed, "original");
        return (series.Id, trashed, kept);
    }

    [Fact]
    public async Task Delete_keeping_files_leaves_the_trashed_originals()
    {
        var (seriesId, trashed, kept) = Seed();

        using (var db = _db.NewContext())
        {
            Assert.IsType<NoContentResult>(await Controller(db).Delete(seriesId, deleteFiles: false, default));
        }

        Assert.True(File.Exists(trashed));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public async Task Delete_with_files_removes_the_trash_too()
    {
        var (seriesId, trashed, kept) = Seed();

        using (var db = _db.NewContext())
        {
            Assert.IsType<NoContentResult>(await Controller(db).Delete(seriesId, deleteFiles: true, default));
        }

        Assert.False(File.Exists(trashed));
        Assert.False(File.Exists(kept));
    }
}
