using Maki.Api.Configuration;
using Maki.Api.Controllers;
using Maki.Api.Jobs;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Sources;
using Maki.Core.Storage;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The three user deletes send files to the bin rather than deleting them, a restore puts them
/// back, and nothing along the way can leave a file in neither place.
/// </summary>
[Collection(ConfigDirCollection.Name)]
public class RecycleBinTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "maki-recycle-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly string? _previousConfigDir = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
    private readonly FakeAppSettings _settings = new();

    public RecycleBinTests()
    {
        _root = Path.Combine(_configDir, "library");
        Directory.CreateDirectory(Path.Combine(_root, "Berserk"));
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

    private sealed class ScriptedMover(Func<string, string, MoveOutcome?> script) : RecycleBinMover
    {
        public override MoveOutcome Move(string source, string target) => script(source, target) ?? base.Move(source, target);
    }

    private static RecycleBinMover CrossVolume() => new ScriptedMover((_, _) => new MoveOutcome(MoveResult.CrossVolume));

    /// <summary>Probes go through; any move of a real comic fails, as a locked file would.</summary>
    private static RecycleBinMover FailingMoves() => new ScriptedMover((source, _) =>
        Path.GetFileName(source).StartsWith(".maki-bin-probe", StringComparison.Ordinal)
            ? null
            : new MoveOutcome(MoveResult.Failed, new IOException("locked")));

    private RecycleBinService Bin(MakiDbContext db, RecycleBinMover? mover = null) =>
        TestRecycleBin.For(db, _settings, mover);

    private ChapterFileDeletion Deletion(MakiDbContext db, RecycleBinMover? mover = null) => new(
        db, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance), TimeProvider.System,
        Bin(db, mover), NullLogger<ChapterFileDeletion>.Instance);

    private ChapterController Chapters(MakiDbContext db) => new(
        new TestLocalizer(),
        db,
        new DownloadQueueService(_db.ScopeFactory(), TimeProvider.System, null!, NullLogger<DownloadQueueService>.Instance),
        new StatsEventService(db),
        new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
        new SourceRegistry([]),
        new SourceAvailability(new FakeAppSettings(), new SourceRegistry([])),
        new SourceChapterListCache(TimeProvider.System, NullLogger<SourceChapterListCache>.Instance),
        new DownloadBatchNotifier(
            new RecordingNotifications(), new RecordingInbox(), new TestLocalizer(),
            new TestUserLocaleResolver(), TimeProvider.System,
            NullLogger<DownloadBatchNotifier>.Instance),
        new TestCurrentUser(1),
        NullLogger<ChapterController>.Instance);

    private SeriesController SeriesApi(MakiDbContext db) => new(
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
        readingProfiles: null!,
        readingTimeEstimates: null!,
        sourceAvailability: null!,
        currentUser: new TestCurrentUser(1),
        userSettings: null!,
        notifications: new RecordingNotifications(),
        locales: new TestUserLocaleResolver(),
        logger: NullLogger<SeriesController>.Instance);

    private sealed record Seeded(int SeriesId, int ChapterId, int SecondChapterId, int FileId, string RelativePath, string Absolute);

    /// <summary>A series with a volume file backing two chapters.</summary>
    private Seeded Seed()
    {
        using var db = _db.NewContext();
        var root = new RootFolder { Path = _root };
        db.RootFolders.Add(root);
        db.SaveChanges();
        var series = new Series { Title = "Berserk", SortTitle = "berserk", FolderName = "Berserk", RootFolderId = root.Id };
        db.Series.Add(series);
        db.SaveChanges();

        var relative = Path.Combine("Berserk", "Berserk v01.cbz");
        var absolute = Path.Combine(_root, relative);
        File.WriteAllText(absolute, "volume one");
        var file = new ChapterFile
        {
            SeriesId = series.Id, RelativePath = relative, Size = 10, SourceName = "Manual", Group = "lfp",
            Trusted = true, PageCount = 200, DateAdded = DateTime.UtcNow.AddDays(-30)
        };
        db.ChapterFiles.Add(file);
        db.SaveChanges();
        var first = new Chapter { SeriesId = series.Id, Number = 1, NumberRaw = "1", Volume = 1, ChapterFileId = file.Id };
        var second = new Chapter { SeriesId = series.Id, Number = 2, NumberRaw = "2", Volume = 1, ChapterFileId = file.Id };
        db.Chapters.AddRange(first, second);
        db.SaveChanges();
        return new Seeded(series.Id, first.Id, second.Id, file.Id, relative, absolute);
    }

    private string BinFile(RecycleBinEntry entry) => Path.Combine(_root, entry.BinPath);

    private static string? Code(IActionResult result) =>
        (result as ObjectResult)?.Value?.GetType().GetProperty("code")?.GetValue(((ObjectResult)result).Value) as string;

    [Fact]
    public async Task Delete_file_moves_the_file_into_the_bin_and_records_it()
    {
        var s = Seed();

        using (var db = _db.NewContext())
        {
            var result = Assert.IsType<OkObjectResult>(await Chapters(db).DeleteFiles([s.ChapterId], Deletion(db), default));
            Assert.Equal(1, Assert.IsType<ChapterFileDeletion.Result>(result.Value).Deleted);
        }

        Assert.False(File.Exists(s.Absolute));
        using var check = _db.NewContext();
        var entry = Assert.Single(check.RecycleBin);
        Assert.Equal("volume one", File.ReadAllText(BinFile(entry)));
        Assert.StartsWith(".maki-trash/bin/", entry.BinPath);
        Assert.Equal(RecycleReason.DeleteFile, entry.Reason);
        Assert.Equal(s.RelativePath, entry.RelativePath);
        Assert.Equal(s.Absolute, entry.OriginalPath);
        Assert.Equal("Berserk", entry.SeriesTitle);
        Assert.Equal(s.FileId, entry.ChapterFileId);
        Assert.Equal(1, entry.DeletedByUserId);
        Assert.Contains("\"group\":\"lfp\"", entry.FileJson);
        Assert.Equal([s.ChapterId, s.SecondChapterId], RecycleBinService.Chapters(entry).Select(c => c.Id).Order());
        Assert.Empty(check.ChapterFiles);
        Assert.All(check.Chapters, c => Assert.NotNull(c.FileRemovedAt));
    }

    [Fact]
    public async Task Remove_chapter_moves_the_file_into_the_bin_and_records_it()
    {
        var s = Seed();

        using (var db = _db.NewContext())
        {
            Assert.IsType<OkObjectResult>(await Chapters(db).Delete([s.ChapterId, s.SecondChapterId], Deletion(db), default));
        }

        Assert.False(File.Exists(s.Absolute));
        using var check = _db.NewContext();
        var entry = Assert.Single(check.RecycleBin);
        Assert.True(File.Exists(BinFile(entry)));
        Assert.Equal(RecycleReason.RemoveChapter, entry.Reason);
        Assert.Equal(2, RecycleBinService.Chapters(entry).Count);
        Assert.Empty(check.Chapters);
        Assert.Empty(check.ChapterFiles);
    }

    [Fact]
    public async Task Series_delete_with_files_bins_every_file_whatever_its_extension()
    {
        var s = Seed();
        var folder = Path.Combine(_root, "Berserk");
        // A linked chapter Maki did not name, the original a repack keeps, a plain zip, a note and a
        // subfolder: none of them is a .cbz, and every one is the user's.
        var others = new[]
        {
            Path.Combine(folder, "Extra c999.cbz"),
            Path.Combine(folder, "Berserk v02.cbr"),
            Path.Combine(folder, "scans.zip"),
            Path.Combine(folder, "notes.txt"),
            Path.Combine(folder, "Extras", "Art", "poster.png"),
        };
        foreach (var path in others)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Path.GetFileName(path));
        }

        File.WriteAllText(Path.Combine(folder, "cover.jpg"), "jpg");
        File.WriteAllText(Path.Combine(folder, "ComicInfo.xml"), "<x/>");

        using (var db = _db.NewContext())
        {
            Assert.IsType<NoContentResult>(await SeriesApi(db).Delete(s.SeriesId, deleteFiles: true, Deletion(db), default));
        }

        Assert.False(Directory.Exists(folder));
        using var check = _db.NewContext();
        Assert.Empty(check.Series);
        var entries = check.RecycleBin.OrderBy(e => e.Id).ToList();
        Assert.Equal(others.Length + 1, entries.Count);
        Assert.All(entries, e =>
        {
            Assert.Equal(RecycleReason.SeriesDelete, e.Reason);
            Assert.True(File.Exists(BinFile(e)));
        });
        Assert.Equal(
            others.Select(p => Path.GetRelativePath(_root, p)).Append(s.RelativePath).Order(),
            entries.Select(e => e.RelativePath).Order());
        Assert.NotNull(Assert.Single(entries, e => e.ChapterFileId == s.FileId).FileJson);
    }

    [Fact]
    public async Task Series_delete_keeps_the_folder_when_a_file_arrives_mid_delete()
    {
        var s = Seed();
        var late = Path.Combine(_root, "Berserk", "Berserk v03.cbz");
        var mover = new ScriptedMover((source, _) =>
        {
            if (source == s.Absolute)
            {
                File.WriteAllText(late, "late");
            }

            return null;
        });

        using (var db = _db.NewContext())
        {
            Assert.IsType<NoContentResult>(await SeriesApi(db).Delete(s.SeriesId, deleteFiles: true, Deletion(db, mover), default));
        }

        Assert.Equal("late", File.ReadAllText(late));
        using var check = _db.NewContext();
        Assert.True(File.Exists(BinFile(Assert.Single(check.RecycleBin))));
    }

    [Fact]
    public async Task Series_delete_leaves_a_file_another_series_claims()
    {
        var s = Seed();
        using (var seed = _db.NewContext())
        {
            var other = new Series
            {
                Title = "Other", SortTitle = "other", FolderName = "Other", RootFolderId = seed.RootFolders.Single().Id
            };
            seed.Series.Add(other);
            seed.SaveChanges();
            seed.ChapterFiles.Add(new ChapterFile
            {
                SeriesId = other.Id, RelativePath = s.RelativePath, SourceName = "Manual", DateAdded = DateTime.UtcNow
            });
            seed.SaveChanges();
        }

        using (var db = _db.NewContext())
        {
            Assert.IsType<NoContentResult>(await SeriesApi(db).Delete(s.SeriesId, deleteFiles: true, Deletion(db), default));
        }

        Assert.Equal("volume one", File.ReadAllText(s.Absolute));
        using var check = _db.NewContext();
        Assert.Empty(check.RecycleBin);
    }

    [Fact]
    public async Task Restore_puts_the_file_back_and_relinks_its_chapters()
    {
        var s = Seed();
        using (var db = _db.NewContext())
        {
            await Chapters(db).DeleteFiles([s.ChapterId], Deletion(db), default);
        }

        int entryId;
        using (var db = _db.NewContext())
        {
            entryId = db.RecycleBin.Single().Id;
            var result = await Bin(db).RestoreAsync(entryId, default);
            Assert.Equal(RecycleBinService.RestoreStatus.Linked, result.Status);
            Assert.Equal(2, result.ChaptersLinked);
        }

        Assert.Equal("volume one", File.ReadAllText(s.Absolute));
        using var check = _db.NewContext();
        Assert.Empty(check.RecycleBin);
        var file = Assert.Single(check.ChapterFiles);
        Assert.Equal(s.RelativePath, file.RelativePath);
        Assert.Equal("lfp", file.Group);
        Assert.True(file.Trusted);
        Assert.Equal(200, file.PageCount);
        Assert.All(check.Chapters, c =>
        {
            Assert.Equal(file.Id, c.ChapterFileId);
            Assert.Null(c.FileRemovedAt);
        });
        var scan = Assert.Single(check.HealthScans);
        Assert.Equal(s.SeriesId, scan.SeriesId);
    }

    [Fact]
    public async Task A_binned_link_whose_target_is_gone_can_still_be_restored()
    {
        var s = Seed();
        var target = Path.Combine(_configDir, "elsewhere.cbz");
        File.WriteAllText(target, "elsewhere");
        File.Delete(s.Absolute);
        if (!Maki.Core.Tests.TestLinks.TryLinkFile(s.Absolute, target))
        {
            return;
        }

        using (var db = _db.NewContext())
        {
            await Chapters(db).DeleteFiles([s.ChapterId], Deletion(db), default);
        }

        File.Delete(target);
        using (var db = _db.NewContext())
        {
            var result = await Bin(db).RestoreAsync(db.RecycleBin.Single().Id, default);
            Assert.NotEqual(RecycleBinService.RestoreStatus.FileMissing, result.Status);
        }

        Assert.NotNull(new FileInfo(s.Absolute).LinkTarget);
    }

    [Fact]
    public async Task Restore_refuses_when_something_now_sits_at_the_original_path()
    {
        var s = Seed();
        using (var db = _db.NewContext())
        {
            await Chapters(db).DeleteFiles([s.ChapterId], Deletion(db), default);
        }

        File.WriteAllText(s.Absolute, "a new download");
        using (var db = _db.NewContext())
        {
            var api = new RecycleBinController(db, Bin(db), _settings, new TestLocalizer());
            var result = await api.Restore(db.RecycleBin.Single().Id, default);
            Assert.IsType<ConflictObjectResult>(result);
            Assert.Equal("error.recycleBin.targetExists", Code(result));
        }

        Assert.Equal("a new download", File.ReadAllText(s.Absolute));
        using var check = _db.NewContext();
        var entry = Assert.Single(check.RecycleBin);
        Assert.Equal("volume one", File.ReadAllText(BinFile(entry)));
    }

    [Fact]
    public async Task Restore_of_a_removed_chapter_puts_the_file_back_unlinked()
    {
        var s = Seed();
        using (var db = _db.NewContext())
        {
            await Chapters(db).Delete([s.ChapterId, s.SecondChapterId], Deletion(db), default);
        }

        using (var db = _db.NewContext())
        {
            var result = await Bin(db).RestoreAsync(db.RecycleBin.Single().Id, default);
            Assert.Equal(RecycleBinService.RestoreStatus.Unlinked, result.Status);
        }

        Assert.True(File.Exists(s.Absolute));
        using var check = _db.NewContext();
        Assert.Empty(check.ChapterFiles);
        Assert.Empty(check.RecycleBin);
        // A root scan is what turns the file into an unlinked finding Health can import.
        Assert.Null(Assert.Single(check.HealthScans).SeriesId);
    }

    [Fact]
    public async Task Restore_after_the_series_is_gone_recreates_the_folder_and_leaves_the_file_unlinked()
    {
        var s = Seed();
        using (var db = _db.NewContext())
        {
            await SeriesApi(db).Delete(s.SeriesId, deleteFiles: true, Deletion(db), default);
        }

        Assert.False(File.Exists(s.Absolute));
        using (var db = _db.NewContext())
        {
            var entry = db.RecycleBin.Single();
            Assert.Equal(RecycleBinService.RestoreStatus.Unlinked, (await Bin(db).RestoreAsync(entry.Id, default)).Status);
        }

        Assert.Equal("volume one", File.ReadAllText(s.Absolute));
        using var check = _db.NewContext();
        Assert.Empty(check.ChapterFiles);
        Assert.Empty(check.RecycleBin);
    }

    [Fact]
    public async Task Housekeeping_purges_bin_entries_past_retention()
    {
        var s = Seed();
        using (var db = _db.NewContext())
        {
            await Chapters(db).DeleteFiles([s.ChapterId], Deletion(db), default);
        }

        var freshPath = Path.Combine(_root, "Berserk", "Berserk v02.cbz");
        File.WriteAllText(freshPath, "volume two");
        File.SetLastWriteTimeUtc(freshPath, DateTime.UtcNow.AddYears(-2));
        string oldBin, freshBin;
        using (var db = _db.NewContext())
        {
            var old = db.RecycleBin.Single();
            old.DeletedAtUtc = DateTime.UtcNow.AddDays(-15);
            oldBin = BinFile(old);
            var series = db.Series.Include(x => x.RootFolder).Single();
            var bin = Bin(db);
            var fresh = bin.Record(series, _root, Path.Combine("Berserk", "Berserk v02.cbz"), freshPath, null, [],
                RecycleReason.DeleteFile);
            db.SaveChanges();
            Assert.True(await bin.MoveInAsync(fresh, default));
            freshBin = BinFile(fresh);
        }

        using (var db = _db.NewContext())
        {
            var job = new HousekeepingJob(db, new AppPaths(),
                new UpgradeTrashService(db, _settings, NullLogger<UpgradeTrashService>.Instance),
                NullLogger<HousekeepingJob>.Instance);
            await job.Execute(new TestJobContext());
        }

        Assert.False(File.Exists(oldBin));
        // Two years old on disk, but deleted today: retention runs from the deletion.
        Assert.True(File.Exists(freshBin));
        using var check = _db.NewContext();
        Assert.Equal(freshBin, BinFile(Assert.Single(check.RecycleBin)));
    }

    [Fact]
    public async Task A_move_reported_as_failed_that_happened_counts_as_binned()
    {
        var s = Seed();
        var mover = new ScriptedMover((source, target) =>
        {
            if (Path.GetFileName(source).StartsWith(".maki-bin-probe", StringComparison.Ordinal))
            {
                return null;
            }

            File.Move(source, target);
            return new MoveOutcome(MoveResult.Failed, new IOException("retransmitted"));
        });

        using (var db = _db.NewContext())
        {
            var result = Assert.IsType<OkObjectResult>(await Chapters(db).DeleteFiles([s.ChapterId], Deletion(db, mover), default));
            Assert.Equal(1, Assert.IsType<ChapterFileDeletion.Result>(result.Value).Deleted);
        }

        using var check = _db.NewContext();
        Assert.True(File.Exists(BinFile(Assert.Single(check.RecycleBin))));
        Assert.Empty(check.ChapterFiles);
    }

    [Fact]
    public async Task The_orphan_sweep_keeps_a_bin_file_whose_entry_spells_its_path_differently()
    {
        var s = Seed();
        using (var db = _db.NewContext())
        {
            await Chapters(db).DeleteFiles([s.ChapterId], Deletion(db), default);
        }

        string binFile;
        using (var db = _db.NewContext())
        {
            var entry = db.RecycleBin.Single();
            binFile = BinFile(entry);
            // Same tag, the name in another Unicode form: the exact path no longer matches.
            entry.BinPath = entry.BinPath[..(entry.BinPath.LastIndexOf('-') + 1)] + "Berserk v01́.cbz";
            db.SaveChanges();
        }

        File.SetLastWriteTimeUtc(binFile, DateTime.UtcNow.AddYears(-2));
        using (var db = _db.NewContext())
        {
            await new UpgradeTrashService(db, _settings, NullLogger<UpgradeTrashService>.Instance).PurgeAsync(default);
        }

        Assert.True(File.Exists(binFile));
    }

    [Fact]
    public async Task A_bin_on_another_volume_refuses_the_delete_and_touches_nothing()
    {
        var s = Seed();

        using (var db = _db.NewContext())
        {
            var result = await Chapters(db).DeleteFiles([s.ChapterId], Deletion(db, CrossVolume()), default);
            Assert.Equal("error.recycleBin.crossVolume", Code(result));
        }

        using (var db = _db.NewContext())
        {
            var result = await SeriesApi(db).Delete(s.SeriesId, deleteFiles: true, Deletion(db, CrossVolume()), default);
            Assert.Equal("error.recycleBin.crossVolume", Code(result));
        }

        Assert.True(File.Exists(s.Absolute));
        using var check = _db.NewContext();
        Assert.Single(check.Series);
        Assert.Single(check.ChapterFiles);
        Assert.Empty(check.RecycleBin);
        Assert.All(check.Chapters, c => Assert.Null(c.FileRemovedAt));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "Berserk"), ".maki-bin-probe*"));
    }

    [Fact]
    public async Task A_move_that_fails_leaves_the_file_and_its_rows_where_they_were()
    {
        var s = Seed();

        using (var db = _db.NewContext())
        {
            var result = Assert.IsType<OkObjectResult>(await Chapters(db).DeleteFiles([s.ChapterId], Deletion(db, FailingMoves()), default));
            Assert.Equal(1, Assert.IsType<ChapterFileDeletion.Result>(result.Value).Failed);
        }

        Assert.True(File.Exists(s.Absolute));
        using var check = _db.NewContext();
        Assert.Empty(check.RecycleBin);
        Assert.Single(check.ChapterFiles);
        Assert.All(check.Chapters, c => Assert.Equal(s.FileId, c.ChapterFileId));
    }

    [Fact]
    public async Task A_series_delete_whose_move_fails_keeps_the_file_and_its_folder()
    {
        var s = Seed();

        using (var db = _db.NewContext())
        {
            Assert.IsType<NoContentResult>(await SeriesApi(db).Delete(s.SeriesId, deleteFiles: true, Deletion(db, FailingMoves()), default));
        }

        Assert.True(File.Exists(s.Absolute));
        using var check = _db.NewContext();
        Assert.Empty(check.RecycleBin);
    }

    /// <summary>Lets the first <c>n</c> saves through and fails the rest.</summary>
    private sealed class SaveBudgetDbContext(DbContextOptions<MakiDbContext> options, int saves) : MakiDbContext(options)
    {
        private int _left = saves;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            _left-- > 0
                ? base.SaveChangesAsync(cancellationToken)
                : throw new InvalidOperationException("Simulated save failure");
    }

    [Fact]
    public async Task A_save_failing_after_the_move_leaves_the_file_recorded_in_the_bin_and_restorable()
    {
        var s = Seed();

        using (var failing = new SaveBudgetDbContext(_db.Options, saves: 1))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => Chapters(failing).DeleteFiles([s.ChapterId], Deletion(failing), default));
        }

        RecycleBinEntry entry;
        using (var db = _db.NewContext())
        {
            entry = db.RecycleBin.Single();
            Assert.True(File.Exists(BinFile(entry)));
            Assert.False(File.Exists(s.Absolute));
            // The row was never removed, so the file reads as missing until it is restored.
            Assert.Single(db.ChapterFiles);
            Assert.Equal(RecycleBinService.RestoreStatus.Linked, (await Bin(db).RestoreAsync(entry.Id, default)).Status);
        }

        Assert.True(File.Exists(s.Absolute));
        using var check = _db.NewContext();
        Assert.Equal(s.FileId, Assert.Single(check.ChapterFiles).Id);
        Assert.Empty(check.RecycleBin);
    }

    [Fact]
    public async Task A_restore_whose_move_fails_keeps_the_file_in_the_bin()
    {
        var s = Seed();
        using (var db = _db.NewContext())
        {
            await Chapters(db).DeleteFiles([s.ChapterId], Deletion(db), default);
        }

        using (var db = _db.NewContext())
        {
            var result = await Bin(db, FailingMoves()).RestoreAsync(db.RecycleBin.Single().Id, default);
            Assert.Equal(RecycleBinService.RestoreStatus.Failed, result.Status);
        }

        Assert.False(File.Exists(s.Absolute));
        using var check = _db.NewContext();
        Assert.True(File.Exists(BinFile(Assert.Single(check.RecycleBin))));
    }

    [Fact]
    public async Task Delete_permanently_and_empty_remove_the_files()
    {
        var s = Seed();
        using (var db = _db.NewContext())
        {
            await Chapters(db).Delete([s.ChapterId, s.SecondChapterId], Deletion(db), default);
        }

        string binFile;
        using (var db = _db.NewContext())
        {
            var entry = db.RecycleBin.Single();
            binFile = BinFile(entry);
            var api = new RecycleBinController(db, Bin(db), _settings, new TestLocalizer());
            Assert.IsType<NoContentResult>(await api.Delete(entry.Id, default));
            Assert.IsType<OkObjectResult>(await api.EmptyBin(default));
        }

        Assert.False(File.Exists(binFile));
        using var check = _db.NewContext();
        Assert.Empty(check.RecycleBin);
    }

    [Fact]
    public void Same_volume_move_refuses_to_overwrite()
    {
        var a = Path.Combine(_root, "a.cbz");
        var b = Path.Combine(_root, "b.cbz");
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");

        Assert.Equal(MoveResult.TargetExists, SameVolumeMove.Move(a, b).Result);
        Assert.Equal("a", File.ReadAllText(a));
        Assert.Equal("b", File.ReadAllText(b));

        File.Delete(b);
        Assert.Equal(MoveResult.Moved, SameVolumeMove.Move(a, b).Result);
        Assert.False(File.Exists(a));
        Assert.Equal("a", File.ReadAllText(b));
    }
}
