using System.IO.Compression;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Import;
using Maki.Core.Inbox;
using Maki.Core.Kavita;
using Maki.Core.Quality;
using Maki.Core.Storage;
using Maki.Core.Reading;
using Maki.Core.Sources;
using SharpCompress.Common;
using SharpCompress.Writers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Importing a finished torrent into a series that already has files. The interesting cases are all
/// about the files the download would displace: whether they're spotted before anything is moved,
/// and what each decision does to them.
/// </summary>
public class TorrentImportServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FakeAppSettings _settings = new();
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "maki-import-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _downloads;

    public TorrentImportServiceTests()
    {
        _downloads = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(_downloads);
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private TorrentImportService Service()
    {
        var db = _db.NewContext();
        var scans = new KavitaScanService(
            new KavitaClient(new StubHttpClientFactory("{}")),
            _settings,
            _db.ScopeFactory(),
            NullLogger<KavitaScanService>.Instance);
        var archives = new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance);
        var registry = new SourceRegistry([]);
        var linker = new CbzLinkService(
            db,
            registry,
            scans,
            new StatsEventService(db),
            archives,
            new SourceAvailability(_settings, registry),
            TestQuality.Create(registry),
            _settings,
            NullLogger<CbzLinkService>.Instance);
        var rename = new SeriesRenameService(
            db, new NamingService(_settings), scans, new TestLocalizer(), NullLogger<SeriesRenameService>.Instance);

        return new TorrentImportService(
            db, null!, null!, linker, rename, archives, _settings,
            new UpgradeEvaluationService(db, TestQuality.Create(registry)), _inbox,
            NullLogger<TorrentImportService>.Instance);
    }

    private readonly RecordingInbox _inbox = new();

    /// <summary>A series with chapters 1-6 of volume 1, each backed by its own file on disk.</summary>
    private (Series Series, DownloadQueueItem Item) SeedLibrary(bool withFiles = true)
    {
        using var db = _db.NewContext();
        var rootFolder = new RootFolder { Path = _root };
        db.RootFolders.Add(rootFolder);
        db.SaveChanges();

        var series = new Series
        {
            Title = "Berserk",
            SortTitle = "Berserk",
            FolderName = "Berserk",
            RootFolderId = rootFolder.Id
        };
        db.Series.Add(series);
        db.SaveChanges();
        Directory.CreateDirectory(Path.Combine(_root, "Berserk"));

        foreach (var number in Enumerable.Range(1, 6))
        {
            var chapter = new Chapter { SeriesId = series.Id, Number = number, Volume = 1, Language = "en" };
            db.Chapters.Add(chapter);
            db.SaveChanges();

            if (!withFiles)
            {
                continue;
            }

            var relative = Path.Combine("Berserk", $"Berserk Vol.1 Ch.{number}.cbz");
            WriteCbz(Path.Combine(_root, relative), [$"Berserk - c00{number} - p001.png"]);
            var file = new ChapterFile
            {
                SeriesId = series.Id,
                RelativePath = relative,
                Size = new FileInfo(Path.Combine(_root, relative)).Length,
                SourceName = "mangadex",
                DateAdded = DateTime.UtcNow
            };
            db.ChapterFiles.Add(file);
            db.SaveChanges();
            chapter.ChapterFileId = file.Id;
            db.SaveChanges();
        }

        var item = new DownloadQueueItem
        {
            SeriesId = series.Id,
            Title = "Berserk v01 (Digital) (1r0n)",
            Protocol = AcquisitionProtocol.Torrent,
            Status = QueueStatus.Downloading
        };
        db.DownloadQueue.Add(item);
        db.SaveChanges();

        // Detached copies: the service resolves its own context, and these only carry ids.
        return (series, item);
    }

    private void Complete(DownloadQueueItem item)
    {
        using var db = _db.NewContext();
        db.DownloadQueue.Single(q => q.Id == item.Id).Status = QueueStatus.Completed;
        db.SaveChanges();
    }

    /// <summary>The downloaded volume: one archive whose page names mark chapters 1-6.</summary>
    private string SeedVolumeDownload(params int[] chapters)
    {
        var path = Path.Combine(_downloads, "Berserk v01 (Digital) (1r0n).cbz");
        WriteCbz(path, chapters.Select(c => $"Berserk - c{c:000} - p001 [Oak].png").ToArray());
        return path;
    }

    private static void WriteCbz(string path, IReadOnlyList<string> pageNames)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var name in pageNames)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write("page");
        }
    }

    [Fact]
    public async Task Plan_reports_the_files_a_volume_download_would_displace()
    {
        var (series, item) = SeedLibrary();
        SeedVolumeDownload(1, 2, 3, 4, 5, 6);

        var plan = await Service().PlanAsync(item, series, _downloads, CancellationToken.None);

        Assert.Null(plan.ErrorKey);
        Assert.True(plan.HasConflicts);
        Assert.Equal(6, plan.ReplacedFileCount);
        Assert.Equal(0, plan.NewChapterCount);

        var file = Assert.Single(plan.Files);
        Assert.Equal("Vol.1", file.Label);
        Assert.Equal(6, file.Chapters.Count);
        Assert.Empty(file.NewChapters);
        Assert.Contains(file.Replaces, r => r.RelativePath.EndsWith("Berserk Vol.1 Ch.1.cbz"));
    }

    /// <summary>
    /// The link step can run twice for one placed file: the poll that imported it was cut off before
    /// the queue row was saved, or a parked item is settled after the job already placed the files.
    /// The second pass finds the file in the folder, skips placing it, and must find its row too.
    /// </summary>
    [Fact]
    public async Task Importing_the_same_download_twice_keeps_one_row_per_file()
    {
        var (series, item) = SeedLibrary(withFiles: false);
        SeedVolumeDownload(1, 2, 3);

        var first = await Service().ImportAsync(
            item, series, _downloads, TorrentImportMode.Replace, CancellationToken.None);
        var second = await Service().ImportAsync(
            item, series, _downloads, TorrentImportMode.Replace, CancellationToken.None);

        Assert.True(first.Applied);
        Assert.True(second.Applied);
        Assert.Equal(1, second.Imported);

        using var db = _db.NewContext();
        var file = Assert.Single(db.ChapterFiles.Where(f => f.SeriesId == series.Id).ToList());
        Assert.All(db.Chapters.Where(c => c.SeriesId == series.Id).ToList(),
            c => Assert.Equal(file.Id, c.ChapterFileId));
        Assert.Equal(1, db.StatsEvents.Count(e => e.SeriesId == series.Id && e.Type == StatsEventType.ChapterDownloaded));
    }

    /// <summary>
    /// A row written on Windows carries a backslash; the same file adopted again under Docker is
    /// spelled with a slash, and the two must still be one row.
    /// </summary>
    [Fact]
    public async Task A_row_stored_with_the_other_separator_is_reused_rather_than_duplicated()
    {
        var (series, item) = SeedLibrary(withFiles: false);
        SeedVolumeDownload(1, 2, 3);
        int existingId;
        using (var db = _db.NewContext())
        {
            var existing = new ChapterFile
            {
                SeriesId = series.Id,
                RelativePath = @"berserk\Berserk v01 (Digital) (1r0n).cbz",
                SourceName = "rescan",
                DateAdded = DateTime.UtcNow
            };
            db.ChapterFiles.Add(existing);
            db.SaveChanges();
            existingId = existing.Id;
        }

        var outcome = await Service().ImportAsync(
            item, series, _downloads, TorrentImportMode.Replace, CancellationToken.None);

        Assert.True(outcome.Applied);
        using var check = _db.NewContext();
        var file = Assert.Single(check.ChapterFiles.Where(f => f.SeriesId == series.Id).ToList());
        Assert.Equal(existingId, file.Id);
        Assert.Equal(Path.Combine("Berserk", "Berserk v01 (Digital) (1r0n).cbz"), file.RelativePath);
        Assert.All(check.Chapters.Where(c => c.SeriesId == series.Id).ToList(),
            c => Assert.Equal(existingId, c.ChapterFileId));
    }

    [Fact]
    public async Task Plan_has_no_conflict_when_the_library_has_no_files_for_those_chapters()
    {
        var (series, item) = SeedLibrary(withFiles: false);
        SeedVolumeDownload(1, 2, 3, 4, 5, 6);

        var plan = await Service().PlanAsync(item, series, _downloads, CancellationToken.None);

        Assert.False(plan.HasConflicts);
        Assert.Equal(6, plan.NewChapterCount);
    }

    [Fact]
    public async Task Replace_imports_and_trashes_the_files_it_supersedes()
    {
        var (series, item) = SeedLibrary();
        SeedVolumeDownload(1, 2, 3, 4, 5, 6);
        var originalBytes = File.ReadAllBytes(Path.Combine(_root, "Berserk", "Berserk Vol.1 Ch.1.cbz"));

        var outcome = await Service().ImportAsync(
            item, series, _downloads, TorrentImportMode.Replace, CancellationToken.None);

        Assert.True(outcome.Applied);
        Assert.Equal(1, outcome.Imported);
        Assert.Equal(6, outcome.Deleted);
        Assert.NotNull(outcome.HistoryGroupId);

        Assert.True(File.Exists(Path.Combine(_root, "Berserk", "Berserk v01 (Digital) (1r0n).cbz")));
        Assert.False(File.Exists(Path.Combine(_root, "Berserk", "Berserk Vol.1 Ch.1.cbz")));

        using var db = _db.NewContext();
        var file = Assert.Single(db.ChapterFiles.Where(f => f.SeriesId == series.Id).ToList());
        Assert.All(db.Chapters.Where(c => c.SeriesId == series.Id).ToList(),
            c => Assert.Equal(file.Id, c.ChapterFileId));

        var history = db.UpgradeHistory.Where(h => h.SeriesId == series.Id).ToList();
        Assert.Equal(6, history.Count);
        Assert.All(history, h => Assert.Equal(outcome.HistoryGroupId, h.GroupId));
        Assert.All(history, h => Assert.StartsWith(".maki-trash/", h.TrashPath));
        Assert.All(history, h => Assert.True(File.Exists(Path.Combine(_root, h.TrashPath!))));
        var first = history.OrderBy(h => h.Id).First();
        Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(_root, first.TrashPath!)));
        Assert.Contains("torrent:", first.AfterJson);
        var detail = VolumeReplacementDetail.Parse(first.DetailJson)!;
        Assert.Equal(file.Id, detail.ReplacementFileId);
        Assert.Single(detail.ChapterIds);
    }

    /// <summary>
    /// The download seeds on. Deleting a library file it replaced must not depend on that file
    /// being the only copy of those bytes, but it must never take a chapter's only file away —
    /// which is why the delete runs off what the chapters point at afterwards.
    /// </summary>
    [Fact]
    public async Task Replace_keeps_a_file_whose_chapters_the_download_did_not_cover()
    {
        var (series, item) = SeedLibrary();
        SeedVolumeDownload(1, 2, 3);

        var outcome = await Service().ImportAsync(
            item, series, _downloads, TorrentImportMode.Replace, CancellationToken.None);

        Assert.True(outcome.Applied);
        Assert.Equal(3, outcome.Deleted);
        Assert.True(File.Exists(Path.Combine(_root, "Berserk", "Berserk Vol.1 Ch.4.cbz")));
        Assert.False(File.Exists(Path.Combine(_root, "Berserk", "Berserk Vol.1 Ch.3.cbz")));
    }

    [Fact]
    public async Task Plan_for_a_partial_volume_lists_only_the_files_its_pages_replace()
    {
        var (series, item) = SeedLibrary();
        SeedVolumeDownload(1, 2, 3);

        var plan = await Service().PlanAsync(item, series, _downloads, CancellationToken.None);

        Assert.Equal(3, plan.ReplacedFileCount);
        var file = Assert.Single(plan.Files);
        Assert.DoesNotContain(file.Replaces, r => r.RelativePath.EndsWith("Berserk Vol.1 Ch.4.cbz"));
    }

    [Fact]
    public async Task SkipExisting_leaves_a_download_that_brings_nothing_new_in_the_download_folder()
    {
        var (series, item) = SeedLibrary();
        SeedVolumeDownload(1, 2, 3, 4, 5, 6);

        var outcome = await Service().ImportAsync(
            item, series, _downloads, TorrentImportMode.SkipExisting, CancellationToken.None);

        Assert.True(outcome.Applied);
        Assert.Equal(0, outcome.Imported);
        Assert.Equal(1, outcome.Skipped);
        Assert.Equal(0, outcome.Deleted);

        Assert.False(File.Exists(Path.Combine(_root, "Berserk", "Berserk v01 (Digital) (1r0n).cbz")));
        Assert.True(File.Exists(Path.Combine(_root, "Berserk", "Berserk Vol.1 Ch.1.cbz")));

        using var db = _db.NewContext();
        Assert.Equal(6, db.ChapterFiles.Count(f => f.SeriesId == series.Id));
    }

    [Fact]
    public async Task SkipExisting_imports_a_download_that_brings_a_missing_chapter_without_stealing_the_rest()
    {
        var (series, item) = SeedLibrary();
        using (var db = _db.NewContext())
        {
            db.Chapters.Add(new Chapter { SeriesId = series.Id, Number = 7, Volume = 1, Language = "en" });
            db.SaveChanges();
        }

        SeedVolumeDownload(1, 2, 3, 4, 5, 6, 7);

        var outcome = await Service().ImportAsync(
            item, series, _downloads, TorrentImportMode.SkipExisting, CancellationToken.None);

        Assert.True(outcome.Applied);
        Assert.Equal(1, outcome.Imported);
        Assert.Equal(0, outcome.Deleted);
        Assert.True(File.Exists(Path.Combine(_root, "Berserk", "Berserk Vol.1 Ch.1.cbz")));

        using var after = _db.NewContext();
        var imported = after.ChapterFiles
            .Single(f => f.SeriesId == series.Id && f.RelativePath.Contains("v01"));
        var chapters = after.Chapters.Where(c => c.SeriesId == series.Id).ToList();

        // Only the chapter nothing backed moved onto the compilation.
        Assert.Equal(imported.Id, chapters.Single(c => c.Number == 7).ChapterFileId);
        Assert.All(chapters.Where(c => c.Number < 7), c => Assert.NotEqual(imported.Id, c.ChapterFileId));
    }

    [Fact]
    public async Task Naming_renames_an_imported_file_to_the_chapter_format_by_default()
    {
        var (series, item) = SeedLibrary(withFiles: false);
        SeedVolumeDownload(1, 2, 3, 4, 5, 6);

        var service = Service();
        var outcome = await service.ImportAsync(
            item, series, _downloads, TorrentImportMode.Replace, CancellationToken.None);
        // What CompletedDownloadJob does before naming: the rename refuses while the series has an
        // in-flight download, and this item is that download.
        Complete(item);
        await service.ApplyNamingAsync(series, outcome.ImportedPaths, CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(_root, "Berserk", "Berserk v01 (Digital) (1r0n).cbz")));
        Assert.True(File.Exists(Path.Combine(_root, "Berserk", "Berserk Vol.1.cbz")));
    }

    /// <summary>
    /// With library.renameimportedfiles off, an adopted file keeps the name the release gave it —
    /// the same promise importing a series from disk has always made.
    /// </summary>
    [Fact]
    public async Task Naming_leaves_an_imported_file_alone_when_renaming_is_off()
    {
        _settings.Set(SettingKeys.LibraryRenameImportedFiles, "false");
        var (series, item) = SeedLibrary(withFiles: false);
        SeedVolumeDownload(1, 2, 3, 4, 5, 6);

        var service = Service();
        var outcome = await service.ImportAsync(
            item, series, _downloads, TorrentImportMode.Replace, CancellationToken.None);
        // What CompletedDownloadJob does before naming: the rename refuses while the series has an
        // in-flight download, and this item is that download.
        Complete(item);
        await service.ApplyNamingAsync(series, outcome.ImportedPaths, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_root, "Berserk", "Berserk v01 (Digital) (1r0n).cbz")));
        Assert.False(File.Exists(Path.Combine(_root, "Berserk", "Berserk Vol.1.cbz")));

        using var db = _db.NewContext();
        var file = Assert.Single(db.ChapterFiles.Where(f => f.SeriesId == series.Id).ToList());
        Assert.EndsWith("Berserk v01 (Digital) (1r0n).cbz", file.RelativePath);
    }

    /// <summary>
    /// The unattended job plans without the series lock, so a delete can finish in between. The
    /// import must not recreate the deleted folder and fill it with files no row owns.
    /// </summary>
    [Fact]
    public async Task A_series_deleted_after_planning_is_not_imported_and_its_folder_stays_gone()
    {
        var (series, item) = SeedLibrary(withFiles: false);
        SeedVolumeDownload(1, 2, 3);
        var service = Service();
        var plan = await service.PlanAsync(item, series, _downloads, CancellationToken.None);

        using (var db = _db.NewContext())
        {
            db.Series.Remove(db.Series.Single(s => s.Id == series.Id));
            db.SaveChanges();
        }

        Directory.Delete(Path.Combine(_root, "Berserk"), recursive: true);

        var outcome = await service.ImportAsync(
            item, series, _downloads, TorrentImportMode.Replace, CancellationToken.None, plan);

        Assert.False(outcome.Applied);
        Assert.Equal(TorrentImportService.SeriesChangedKey, outcome.ErrorKey);
        Assert.False(Directory.Exists(Path.Combine(_root, "Berserk")));
    }

    [Fact]
    public async Task A_series_moved_after_planning_is_not_imported_into_either_folder()
    {
        var (series, item) = SeedLibrary(withFiles: false);
        SeedVolumeDownload(1, 2, 3);
        var service = Service();
        var plan = await service.PlanAsync(item, series, _downloads, CancellationToken.None);

        using (var db = _db.NewContext())
        {
            db.Series.Single(s => s.Id == series.Id).FolderName = "Berserk (Deluxe)";
            db.SaveChanges();
        }

        var outcome = await service.ImportAsync(
            item, series, _downloads, TorrentImportMode.Replace, CancellationToken.None, plan);

        Assert.False(outcome.Applied);
        Assert.Equal(TorrentImportService.SeriesChangedKey, outcome.ErrorKey);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "Berserk")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Berserk (Deluxe)")));
        using var check = _db.NewContext();
        Assert.Empty(check.ChapterFiles.Where(f => f.SeriesId == series.Id).ToList());
    }

    /// <summary>
    /// An unattended import never writes Importing to its row, so the series and chapter delete
    /// guards learn about it from the importer's registry.
    /// </summary>
    [Fact]
    public async Task A_running_unattended_import_counts_as_in_flight_for_the_delete_guards()
    {
        var (series, _) = SeedLibrary(withFiles: false);
        // An id no other test's queue row will have: the registry is process-wide.
        const int queueId = 918_273;
        using (var seed = _db.NewContext())
        {
            seed.DownloadQueue.Add(new DownloadQueueItem
            {
                Id = queueId,
                SeriesId = series.Id,
                Title = "Berserk v02",
                Protocol = AcquisitionProtocol.Torrent,
                Status = QueueStatus.Downloading
            });
            seed.SaveChanges();
        }

        using var db = _db.NewContext();
        Assert.False(await SeriesLocks.InFlight(db.DownloadQueue).AnyAsync(q => q.Id == queueId));

        TorrentImportService.BeginAutomaticImport(queueId);
        try
        {
            Assert.True(await SeriesLocks.InFlight(db.DownloadQueue).AnyAsync(q => q.SeriesId == series.Id));
        }
        finally
        {
            TorrentImportService.EndAutomaticImport(queueId);
        }

        Assert.False(await SeriesLocks.InFlight(db.DownloadQueue).AnyAsync(q => q.Id == queueId));
    }

    [Fact]
    public async Task A_download_that_is_gone_plans_as_an_error_rather_than_throwing()
    {
        var (series, item) = SeedLibrary();

        var plan = await Service().PlanAsync(
            item, series, Path.Combine(_root, "nope"), CancellationToken.None);

        Assert.NotNull(plan.ErrorKey);
        Assert.False(plan.HasConflicts);
    }

    /// <summary>
    /// A release that is not already a CBZ. The tar stands in for the RAR sets that actually fail
    /// on a real instance: SharpCompress cannot write RAR, and both reach the converter through
    /// the same autodetect. The point of the test is the import path, not the container.
    /// </summary>
    [Fact]
    public async Task An_archive_that_is_not_a_cbz_is_repacked_on_the_way_in()
    {
        var (series, item) = SeedLibrary(withFiles: false);
        WriteTar(
            Path.Combine(_downloads, "Berserk v01 (Digital) (1r0n).cbt"),
            [.. Enumerable.Range(1, 6).Select(c => $"Berserk - c{c:000} - p001 [Oak].png")]);

        var plan = await Service().PlanAsync(item, series, _downloads, CancellationToken.None);
        var planned = Assert.Single(plan.Files);
        Assert.Equal("Berserk v01 (Digital) (1r0n).cbz", planned.FileName);
        Assert.Equal(6, planned.NewChapters.Count);

        var outcome = await Service().ImportAsync(
            item, series, _downloads, TorrentImportMode.Replace, CancellationToken.None);

        Assert.True(outcome.Applied);
        var imported = Path.Combine(_root, "Berserk", "Berserk v01 (Digital) (1r0n).cbz");
        Assert.True(File.Exists(imported));
        Assert.Equal(6, CbzReader.PageNames(imported).Count);

        using var db = _db.NewContext();
        var file = Assert.Single(db.ChapterFiles.Where(f => f.SeriesId == series.Id).ToList());
        Assert.All(db.Chapters.Where(c => c.SeriesId == series.Id).ToList(),
            c => Assert.Equal(file.Id, c.ChapterFileId));
    }

    /// <summary>
    /// The extension lies. A release that is 7z data under a ".cbz" name used to be opened as a
    /// zip, read as empty and dropped from the plan, or worse placed as-is into the library where
    /// no reader could open it. The bytes decide: it is repacked into a real zip, and the original
    /// in the download folder is untouched because the torrent is still seeding from it.
    /// </summary>
    [Fact]
    public async Task A_7z_named_cbz_is_repacked_rather_than_placed()
    {
        var (series, item) = SeedLibrary(withFiles: false);
        var disguised = Path.Combine(_downloads, "Berserk v01 (Digital) (1r0n).cbz");
        File.WriteAllBytes(disguised, Convert.FromBase64String(SevenZipOfSixPages));
        var originalBytes = File.ReadAllBytes(disguised);
        Assert.Equal(ArchiveSignature.Container.Other, ArchiveSignature.Sniff(disguised));

        var plan = await Service().PlanAsync(item, series, _downloads, CancellationToken.None);
        Assert.Null(plan.ErrorKey);
        var planned = Assert.Single(plan.Files);
        Assert.Equal(6, planned.NewChapters.Count);

        var outcome = await Service().ImportAsync(
            item, series, _downloads, TorrentImportMode.Replace, CancellationToken.None);

        Assert.True(outcome.Applied, outcome.Error ?? outcome.ErrorKey);
        var imported = Path.Combine(_root, "Berserk", "Berserk v01 (Digital) (1r0n).cbz");
        Assert.Equal(ArchiveSignature.Container.Zip, ArchiveSignature.Sniff(imported));
        Assert.Equal(6, CbzReader.PageNames(imported).Count);

        // The seeding copy keeps its bytes and its name; the library got a new file, not a rewrite.
        Assert.Equal(originalBytes, File.ReadAllBytes(disguised));
        Assert.NotEqual(new FileInfo(disguised).Length, new FileInfo(imported).Length);

        using var db = _db.NewContext();
        var file = Assert.Single(db.ChapterFiles.Where(f => f.SeriesId == series.Id).ToList());
        Assert.All(db.Chapters.Where(c => c.SeriesId == series.Id).ToList(),
            c => Assert.Equal(file.Id, c.ChapterFileId));
    }

    /// <summary>
    /// A stored 7z holding "Berserk - c001 - p001 [Oak].png" through c006, each four bytes of
    /// "page". SharpCompress reads 7z but cannot write it, so the fixture is baked in.
    /// </summary>
    private const string SevenZipOfSixPages =
        "N3q8ryccAATdwB8anwAAAAAAAAAiAAAAAAAAANc5VhFwYWdlcGFnZXBhZ2VwYWdlcGFnZXBhZ2UAAIEzB66tixMmvTI/mh5abFMQpAp7A6IluzdsN1W0E+1RSHewg6eY/TlNHh/rkz8Sk5MxEUFGXWNdHh7MSC0Xj2CIysesro/C7ufiWbC96LDnx6ZlHs6iKdqVWsjRb0gerTkZC+SMGqw90GRD8Qg7Z6xWgvuF4Nf48FS+jINYpVUEfg6iEQAXBhgBCYCHAAcLAQABIwMBAQVdABAAAAyCJgoBgLU2+wAA";

    /// <summary>A zip is already a CBZ container, so it goes in as it is under the right name.</summary>
    [Fact]
    public async Task A_plain_zip_is_imported_without_being_rebuilt()
    {
        var (series, item) = SeedLibrary(withFiles: false);
        var zip = Path.Combine(_downloads, "Berserk v01 (Digital) (1r0n).zip");
        WriteCbz(zip, [.. Enumerable.Range(1, 6).Select(c => $"Berserk - c{c:000} - p001 [Oak].png")]);

        var outcome = await Service().ImportAsync(
            item, series, _downloads, TorrentImportMode.Replace, CancellationToken.None);

        Assert.True(outcome.Applied);
        var imported = Path.Combine(_root, "Berserk", "Berserk v01 (Digital) (1r0n).cbz");
        Assert.Equal(new FileInfo(zip).Length, new FileInfo(imported).Length);
        Assert.Equal(6, CbzReader.PageNames(imported).Count);
    }

    [Fact]
    public async Task A_folder_of_loose_pages_is_packed_per_folder()
    {
        var (series, item) = SeedLibrary(withFiles: false);
        var volume = Path.Combine(_downloads, "Berserk v01");
        Directory.CreateDirectory(volume);
        foreach (var chapter in Enumerable.Range(1, 6))
        {
            File.WriteAllText(Path.Combine(volume, $"Berserk - c{chapter:000} - p001.png"), "page");
        }

        var outcome = await Service().ImportAsync(
            item, series, _downloads, TorrentImportMode.Replace, CancellationToken.None);

        Assert.True(outcome.Applied);
        var imported = Path.Combine(_root, "Berserk", "Berserk v01.cbz");
        Assert.True(File.Exists(imported));
        Assert.Equal(6, CbzReader.PageNames(imported).Count);
    }

    /// <summary>
    /// A download of something Maki cannot open used to report "No CBZ files found", which reads
    /// exactly like a download that arrived empty and sends the user looking for the wrong problem.
    /// </summary>
    [Fact]
    public async Task A_download_with_nothing_readable_in_it_says_what_was_there()
    {
        var (series, item) = SeedLibrary(withFiles: false);
        File.WriteAllText(Path.Combine(_downloads, "volume one.pdf"), "pdf");
        File.WriteAllText(Path.Combine(_downloads, "volume two.pdf"), "pdf");

        var plan = await Service().PlanAsync(item, series, _downloads, CancellationToken.None);

        Assert.Equal("error.torrentImport.noComicsFound", plan.ErrorKey);
        var detail = plan.ErrorArgs?.GetType().GetProperty("detail")?.GetValue(plan.ErrorArgs);
        Assert.Equal("found 2 .pdf", detail);
    }

    /// <summary>
    /// Pins a volume-cutoff profile, marks every file a measured aggregator copy, and makes the item an
    /// upgrade that replaces the files <paramref name="replaces"/> picks (default: every untrusted one).
    /// </summary>
    private void MakeUpgrade(Series series, DownloadQueueItem item, Action<List<ChapterFile>>? files = null,
        Func<List<ChapterFile>, IEnumerable<int>>? replaces = null, string? language = null)
    {
        using var db = _db.NewContext();
        var profile = new UpgradeProfile { Name = "Volumes", Cutoff = QualityTier.Volume, UpgradesEnabled = true };
        UpgradeProfileDefaults.Normalise(profile);
        db.UpgradeProfiles.Add(profile);
        db.SaveChanges();
        db.Series.Single(s => s.Id == series.Id).UpgradeProfileId = profile.Id;
        var rows = db.ChapterFiles.Where(f => f.SeriesId == series.Id).ToList();
        foreach (var row in rows)
        {
            row.Tier = QualityTier.Aggregator;
            row.PageCount = 20;
            row.MedianWidth = 800;
            row.MeasuredAtUtc = DateTime.UtcNow;
        }

        files?.Invoke(rows);
        var info = new TorrentUpgradeInfo
        {
            ProfileId = profile.Id,
            ReplacedFileIds = [.. replaces?.Invoke(rows) ?? rows.Where(f => !f.Trusted).Select(f => f.Id)],
            Language = language
        };
        db.DownloadQueue.Single(q => q.Id == item.Id).UpgradeInfoJson = info.Serialize();
        item.UpgradeInfoJson = info.Serialize();
        db.SaveChanges();
    }

    private void WriteDownload(string name, params int[] chapters) =>
        WriteCbz(Path.Combine(_downloads, name), chapters.Select(c => $"Berserk - c{c:000} - p001 [Oak].png").ToArray());

    private void SplitIntoTwoVolumes(int seriesId)
    {
        using var db = _db.NewContext();
        foreach (var chapter in db.Chapters.Where(c => c.SeriesId == seriesId && c.Number > 3))
        {
            chapter.Volume = 2;
        }

        db.SaveChanges();
    }

    private static bool IsLateChapterFile(ChapterFile f) =>
        new[] { "Ch.4.cbz", "Ch.5.cbz", "Ch.6.cbz" }.Any(n => f.RelativePath.EndsWith(n, StringComparison.Ordinal));

    [Fact]
    public async Task An_upgrade_plan_suggests_skipping_a_file_whose_chapters_are_already_met()
    {
        var (series, item) = SeedLibrary();
        SplitIntoTwoVolumes(series.Id);
        MakeUpgrade(series, item, rows => rows.Where(IsLateChapterFile).ToList().ForEach(f => f.Trusted = true));
        WriteDownload("Berserk v01 (Digital) (1r0n).cbz", 1, 2, 3);
        WriteDownload("Berserk v02 (Digital) (1r0n).cbz", 4, 5, 6);

        var plan = await Service().PlanAsync(item, series, _downloads, CancellationToken.None);

        Assert.True(plan.IsUpgrade);
        Assert.Equal(["Berserk v02 (Digital) (1r0n).cbz"], plan.SuggestedSkips);
        var v01 = plan.Files.Single(f => f.FileName.Contains("v01"));
        var v02 = plan.Files.Single(f => f.FileName.Contains("v02"));
        Assert.Equal((3, 0), (v01.UpgradeCount, v01.AlreadyMetCount));
        Assert.Equal((0, 3), (v02.UpgradeCount, v02.AlreadyMetCount));
    }

    [Fact]
    public async Task An_upgrade_plan_lists_only_the_files_its_verdict_replaces()
    {
        var (series, item) = SeedLibrary();
        SplitIntoTwoVolumes(series.Id);
        MakeUpgrade(series, item, rows => rows.Where(IsLateChapterFile).ToList().ForEach(f => f.Trusted = true));
        WriteDownload("Berserk v01 (Digital) (1r0n).cbz", 1, 2, 3);
        WriteDownload("Berserk v02 (Digital) (1r0n).cbz", 4, 5, 6);

        var plan = await Service().PlanAsync(item, series, _downloads, CancellationToken.None);

        Assert.Equal(3, plan.Files.Single(f => f.FileName.Contains("v01")).Replaces.Count);
        Assert.Empty(plan.Files.Single(f => f.FileName.Contains("v02")).Replaces);
        Assert.Equal(3, plan.ReplacedFileCount);
    }

    [Fact]
    public async Task An_upgrade_for_one_language_never_links_a_chapter_of_another()
    {
        var (series, item) = SeedLibrary();
        int spanishId;
        using (var db = _db.NewContext())
        {
            foreach (var chapter in db.Chapters.Where(c => c.SeriesId == series.Id))
            {
                chapter.Language = "fr";
            }

            var spanish = new Chapter { SeriesId = series.Id, Number = 2, Volume = 1, Language = "es" };
            db.Chapters.Add(spanish);
            db.SaveChanges();
            spanishId = spanish.Id;
        }

        MakeUpgrade(series, item, language: "fr");
        SeedVolumeDownload(1, 2, 3, 4, 5, 6);

        var outcome = await Service().ImportAsync(item, series, _downloads, TorrentImportMode.Replace, CancellationToken.None);

        Assert.True(outcome.Applied);
        using var after = _db.NewContext();
        var imported = after.ChapterFiles.Single(f => f.SeriesId == series.Id && f.RelativePath.Contains("v01"));
        var chapters = after.Chapters.Where(c => c.SeriesId == series.Id).ToList();
        Assert.Null(chapters.Single(c => c.Id == spanishId).ChapterFileId);
        Assert.All(chapters.Where(c => c.Language == "fr"), c => Assert.Equal(imported.Id, c.ChapterFileId));
    }

    [Fact]
    public async Task A_plain_download_plans_no_upgrade_and_suggests_nothing()
    {
        var (series, item) = SeedLibrary();
        SeedVolumeDownload(1, 2, 3, 4, 5, 6);

        var plan = await Service().PlanAsync(item, series, _downloads, CancellationToken.None);

        Assert.False(plan.IsUpgrade);
        Assert.Empty(plan.SuggestedSkips);
        Assert.All(plan.Files, f => Assert.Equal((0, 0), (f.UpgradeCount, f.AlreadyMetCount)));
    }

    [Fact]
    public async Task The_skip_set_leaves_the_named_file_out_of_the_import()
    {
        var (series, item) = SeedLibrary();
        SplitIntoTwoVolumes(series.Id);
        MakeUpgrade(series, item, replaces: rows => rows.Where(f => !IsLateChapterFile(f)).Select(f => f.Id));
        WriteDownload("Berserk v01 (Digital) (1r0n).cbz", 1, 2, 3);
        WriteDownload("Berserk v02 (Digital) (1r0n).cbz", 4, 5, 6);

        var outcome = await Service().ImportAsync(item, series, _downloads, TorrentImportMode.Replace,
            CancellationToken.None, skipFiles: new HashSet<string> { "Berserk v02 (Digital) (1r0n).cbz" });

        Assert.True(outcome.Applied);
        Assert.Equal(1, outcome.Imported);
        Assert.Equal(3, outcome.Deleted);
        Assert.False(File.Exists(Path.Combine(_root, "Berserk", "Berserk v02 (Digital) (1r0n).cbz")));
        Assert.True(File.Exists(Path.Combine(_root, "Berserk", "Berserk Vol.1 Ch.4.cbz")));
        var info = TorrentUpgradeInfo.Parse(item.UpgradeInfoJson)!;
        Assert.Equal(TorrentUpgradeOutcomes.Applied, info.Outcome);
        Assert.Equal(outcome.HistoryGroupId, info.HistoryGroupId);
        var raised = Assert.Single(_inbox.RaisedForSeries);
        Assert.Equal(InboxEventType.VolumeUpgraded, raised.Type);
    }

    [Fact]
    public async Task The_guard_parks_an_upgrade_whose_volume_cannot_be_measured()
    {
        var (series, item) = SeedLibrary();
        MakeUpgrade(series, item);
        SeedVolumeDownload(1, 2, 3, 4, 5, 6);
        var service = Service();
        var plan = await service.PlanAsync(item, series, _downloads, CancellationToken.None);

        var failure = await service.CheckVolumeGuardAsync(item, series, _downloads, plan, CancellationToken.None);

        Assert.Equal(new VolumeGuardFailure("Berserk v01 (Digital) (1r0n).cbz", "unmeasurable"), failure);
        TorrentImportService.ParkForGuard(item, failure!);
        Assert.Equal(QueueStatus.AwaitingImport, item.Status);
        Assert.Equal("error.upgrades.volumeGuard", item.ErrorKey);
        Assert.Contains("unmeasurable", item.ErrorParamsJson);
        Assert.Equal(TorrentUpgradeOutcomes.Parked, TorrentUpgradeInfo.Parse(item.UpgradeInfoJson)!.Outcome);
    }

    [Theory]
    [InlineData(60, "fewer_pages")]
    [InlineData(120, null)]
    public async Task The_guard_parks_a_volume_with_fewer_pages_than_the_files_it_replaces(int pages, string? reason)
    {
        var (series, item) = SeedLibrary();
        MakeUpgrade(series, item);
        TestQuality.WriteCbz(Path.Combine(_downloads, "Berserk v01 (Digital) (1r0n).cbz"), pages, 40, 60);
        var service = Service();
        var plan = await service.PlanAsync(item, series, _downloads, CancellationToken.None);

        var failure = await service.CheckVolumeGuardAsync(item, series, _downloads, plan, CancellationToken.None);

        Assert.Equal(reason, failure?.Reason);
    }

    [Fact]
    public async Task An_upgrade_leaves_a_protected_file_inside_the_volume_span_alone()
    {
        var (series, item) = SeedLibrary();
        MakeUpgrade(series, item, rows => rows.Single(f => f.RelativePath.EndsWith("Ch.4.cbz", StringComparison.Ordinal)).Trusted = true);
        SeedVolumeDownload(1, 2, 3, 4, 5, 6);

        var outcome = await Service().ImportAsync(
            item, series, _downloads, TorrentImportMode.Replace, CancellationToken.None);

        Assert.True(outcome.Applied);
        Assert.Equal(5, outcome.Deleted);
        var protectedPath = Path.Combine(_root, "Berserk", "Berserk Vol.1 Ch.4.cbz");
        Assert.True(File.Exists(protectedPath));
        using var db = _db.NewContext();
        var protectedFile = db.ChapterFiles.Single(f => f.RelativePath.EndsWith("Ch.4.cbz"));
        var chapters = db.Chapters.Where(c => c.SeriesId == series.Id).ToList();
        Assert.Equal(protectedFile.Id, chapters.Single(c => c.Number == 4).ChapterFileId);
        var volume = db.ChapterFiles.Single(f => f.RelativePath.Contains("v01"));
        Assert.All(chapters.Where(c => c.Number != 4), c => Assert.Equal(volume.Id, c.ChapterFileId));
    }

    [Fact]
    public async Task An_upgrade_naming_no_files_parks_like_any_torrent()
    {
        var (series, item) = SeedLibrary();
        MakeUpgrade(series, item, replaces: _ => []);
        SeedVolumeDownload(1, 2, 3, 4, 5, 6);
        var service = Service();
        var plan = await service.PlanAsync(item, series, _downloads, CancellationToken.None);

        var decision = await service.DecideUnattendedAsync(item, series, _downloads, plan, CancellationToken.None);

        Assert.Equal(new UnattendedDecision(true, null, null), decision);
        Assert.Equal(TorrentUpgradeOutcomes.Pending, TorrentUpgradeInfo.Parse(item.UpgradeInfoJson)!.Outcome);
    }

    [Fact]
    public async Task An_upgrade_that_passes_the_guard_imports_unattended_with_its_skips()
    {
        var (series, item) = SeedLibrary();
        MakeUpgrade(series, item);
        TestQuality.WriteCbz(Path.Combine(_downloads, "Berserk v01 (Digital) (1r0n).cbz"), 120, 40, 60);
        var service = Service();
        var plan = await service.PlanAsync(item, series, _downloads, CancellationToken.None);

        var decision = await service.DecideUnattendedAsync(item, series, _downloads, plan, CancellationToken.None);

        Assert.False(decision.Park);
        Assert.NotNull(decision.SkipFiles);
    }

    [Fact]
    public async Task A_grabbed_proposal_parks_with_its_skips_filled_in()
    {
        var (series, item) = SeedLibrary();
        MakeUpgrade(series, item);
        using (var db = _db.NewContext())
        {
            var info = TorrentUpgradeInfo.Parse(item.UpgradeInfoJson)!;
            info.ProposalId = 42;
            item.UpgradeInfoJson = info.Serialize();
            db.DownloadQueue.Single(q => q.Id == item.Id).UpgradeInfoJson = item.UpgradeInfoJson;
            db.SaveChanges();
        }

        TestQuality.WriteCbz(Path.Combine(_downloads, "Berserk v01 (Digital) (1r0n).cbz"), 120, 40, 60);
        var service = Service();
        var plan = await service.PlanAsync(item, series, _downloads, CancellationToken.None);

        var decision = await service.DecideUnattendedAsync(item, series, _downloads, plan, CancellationToken.None);

        Assert.Equal(new UnattendedDecision(true, null, null), decision);
        Assert.Equal(plan.SuggestedSkips, TorrentUpgradeInfo.Parse(item.UpgradeInfoJson)!.SkipFileNames);
    }

    [Fact]
    public async Task The_guard_measures_a_chapter_file_that_replaces_one_too()
    {
        var (series, item) = SeedLibrary();
        MakeUpgrade(series, item);
        TestQuality.WriteCbz(Path.Combine(_downloads, "Berserk c001 (Digital).cbz"), 5, 40, 60);
        var service = Service();
        var plan = await service.PlanAsync(item, series, _downloads, CancellationToken.None);

        var failure = await service.CheckVolumeGuardAsync(item, series, _downloads, plan, CancellationToken.None);

        Assert.Equal(new VolumeGuardFailure("Berserk c001 (Digital).cbz", "fewer_pages"), failure);
    }

    [Fact]
    public async Task A_revert_hands_a_chapter_back_to_a_file_the_import_did_not_supersede()
    {
        var (series, item) = SeedLibrary();
        int sharedId;
        using (var db = _db.NewContext())
        {
            var chapters = db.Chapters.Where(c => c.SeriesId == series.Id).ToList();
            sharedId = chapters.Single(c => c.Number == 5).ChapterFileId!.Value;
            var six = chapters.Single(c => c.Number == 6);
            six.ChapterFileId = sharedId;
            six.Volume = 2;
            db.SaveChanges();
        }

        SeedVolumeDownload(1, 2, 3, 4, 5);
        var outcome = await Service().ImportAsync(
            item, series, _downloads, TorrentImportMode.Replace, CancellationToken.None);
        Assert.Equal(4, outcome.Deleted);

        using (var db = _db.NewContext())
        {
            Assert.NotEqual(sharedId, db.Chapters.Single(c => c.SeriesId == series.Id && c.Number == 5).ChapterFileId);
            var (_, error) = await new UpgradeRevertService(db, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
                NullLogger<UpgradeRevertService>.Instance).RevertGroupAsync(outcome.HistoryGroupId!.Value, null, CancellationToken.None);
            Assert.Equal(UpgradeRevertError.None, error);
        }

        using var check = _db.NewContext();
        var after = check.Chapters.Where(c => c.SeriesId == series.Id).ToList();
        Assert.Equal(sharedId, after.Single(c => c.Number == 5).ChapterFileId);
        Assert.Equal(sharedId, after.Single(c => c.Number == 6).ChapterFileId);
        Assert.All(after.Where(c => c.Number <= 4), c => Assert.NotNull(c.ChapterFileId));
        Assert.True(File.Exists(Path.Combine(_root, "Berserk", "Berserk Vol.1 Ch.1.cbz")));
    }

    [Fact]
    public async Task A_superseded_file_already_gone_from_disk_writes_no_history_and_the_group_still_reverts()
    {
        var (series, item) = SeedLibrary();
        File.Delete(Path.Combine(_root, "Berserk", "Berserk Vol.1 Ch.2.cbz"));
        SeedVolumeDownload(1, 2, 3, 4, 5, 6);

        var outcome = await Service().ImportAsync(
            item, series, _downloads, TorrentImportMode.Replace, CancellationToken.None);

        Assert.Equal(5, outcome.Deleted);
        using var db = _db.NewContext();
        Assert.Equal(5, db.UpgradeHistory.Count());
        Assert.Single(db.ChapterFiles.Where(f => f.SeriesId == series.Id).ToList());
        var (_, error) = await new UpgradeRevertService(db, new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
            NullLogger<UpgradeRevertService>.Instance).RevertGroupAsync(outcome.HistoryGroupId!.Value, null, CancellationToken.None);
        Assert.Equal(UpgradeRevertError.None, error);
    }

    [Fact]
    public async Task A_superseded_file_that_cannot_move_keeps_its_row_and_gets_no_history()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var (series, item) = SeedLibrary();
        SeedVolumeDownload(1, 2, 3, 4, 5, 6);
        var locked = Path.Combine(_root, "Berserk", "Berserk Vol.1 Ch.3.cbz");
        int lockedId;
        using (var db = _db.NewContext())
        {
            lockedId = db.ChapterFiles.Single(f => f.RelativePath.EndsWith("Ch.3.cbz")).Id;
        }

        TorrentImportOutcome outcome;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            outcome = await Service().ImportAsync(
                item, series, _downloads, TorrentImportMode.Replace, CancellationToken.None);
        }

        Assert.Equal(5, outcome.Deleted);
        Assert.True(File.Exists(locked));
        using var check = _db.NewContext();
        Assert.True(check.ChapterFiles.Any(f => f.Id == lockedId));
        Assert.Equal(5, check.UpgradeHistory.Count());
        Assert.DoesNotContain(check.UpgradeHistory.ToList(), h => h.ChapterFileId == lockedId);
    }

    [Fact]
    public async Task The_guard_ignores_a_download_that_is_not_an_upgrade()
    {
        var (series, item) = SeedLibrary();
        SeedVolumeDownload(1, 2, 3, 4, 5, 6);
        var service = Service();
        var plan = await service.PlanAsync(item, series, _downloads, CancellationToken.None);

        Assert.Null(await service.CheckVolumeGuardAsync(item, series, _downloads, plan, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_volume_displaces_an_older_volume_only_through_the_displaceable_set(bool upgrade)
    {
        var (series, item) = SeedLibrary(withFiles: false);
        int oldId;
        using (var db = _db.NewContext())
        {
            var relative = Path.Combine("Berserk", "Berserk v01 (old scan).cbz");
            WriteCbz(Path.Combine(_root, relative), [.. Enumerable.Range(1, 6).Select(c => $"Berserk - c{c:000} - p001.png")]);
            var old = new ChapterFile
            {
                SeriesId = series.Id, RelativePath = relative, SourceName = "rescan", DateAdded = DateTime.UtcNow,
                Size = new FileInfo(Path.Combine(_root, relative)).Length
            };
            db.ChapterFiles.Add(old);
            db.SaveChanges();
            oldId = old.Id;
            foreach (var chapter in db.Chapters.Where(c => c.SeriesId == series.Id))
            {
                chapter.ChapterFileId = old.Id;
            }

            db.SaveChanges();
        }

        if (upgrade)
        {
            MakeUpgrade(series, item);
        }

        SeedVolumeDownload(1, 2, 3, 4, 5, 6);

        var outcome = await Service().ImportAsync(
            item, series, _downloads, TorrentImportMode.Replace, CancellationToken.None);

        Assert.True(outcome.Applied);
        using var check = _db.NewContext();
        var chapters = check.Chapters.Where(c => c.SeriesId == series.Id).ToList();
        if (upgrade)
        {
            Assert.Equal(1, outcome.Deleted);
            Assert.All(chapters, c => Assert.NotEqual(oldId, c.ChapterFileId));
            Assert.False(File.Exists(Path.Combine(_root, "Berserk", "Berserk v01 (old scan).cbz")));
        }
        else
        {
            Assert.Equal(0, outcome.Deleted);
            Assert.All(chapters, c => Assert.Equal(oldId, c.ChapterFileId));
            Assert.True(File.Exists(Path.Combine(_root, "Berserk", "Berserk v01 (old scan).cbz")));
        }
    }

    [Fact]
    public async Task A_hardlinked_superseded_file_is_moved_to_the_trash_with_its_bytes_intact()
    {
        var (series, item) = SeedLibrary();
        SeedVolumeDownload(1, 2, 3, 4, 5, 6);
        var library = Path.Combine(_root, "Berserk", "Berserk Vol.1 Ch.1.cbz");
        var seeding = Path.Combine(_root, "seeding", "ch1.cbz");
        Directory.CreateDirectory(Path.GetDirectoryName(seeding)!);
        if (!FileLinker.TryHardlink(library, seeding))
        {
            return;
        }

        var bytes = File.ReadAllBytes(seeding);

        var outcome = await Service().ImportAsync(
            item, series, _downloads, TorrentImportMode.Replace, CancellationToken.None);

        Assert.True(outcome.Applied);
        Assert.Equal(bytes, File.ReadAllBytes(seeding));
        using var db = _db.NewContext();
        var trashed = db.UpgradeHistory.AsEnumerable()
            .Single(h => VolumeReplacementDetail.Parse(h.DetailJson)!.RelativePath.EndsWith("Ch.1.cbz", StringComparison.Ordinal));
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_root, trashed.TrashPath!)));
    }

    private static void WriteTar(string path, IReadOnlyList<string> pageNames)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var writer = WriterFactory.OpenWriter(
            stream, ArchiveType.Tar, new WriterOptions(CompressionType.None));
        foreach (var name in pageNames)
        {
            writer.Write(name, new MemoryStream("page"u8.ToArray()), DateTime.UtcNow);
        }
    }
}
