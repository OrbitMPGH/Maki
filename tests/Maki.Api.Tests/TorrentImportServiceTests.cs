using System.IO.Compression;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Kavita;
using Maki.Core.Reading;
using Maki.Core.Sources;
using SharpCompress.Common;
using SharpCompress.Writers;
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
            NullLogger<CbzLinkService>.Instance);
        var rename = new SeriesRenameService(
            db, new NamingService(_settings), scans, NullLogger<SeriesRenameService>.Instance);

        return new TorrentImportService(
            db, null!, null!, linker, rename, archives, _settings,
            NullLogger<TorrentImportService>.Instance);
    }

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

        Assert.Null(plan.Error);
        Assert.True(plan.HasConflicts);
        Assert.Equal(6, plan.ReplacedFileCount);
        Assert.Equal(0, plan.NewChapterCount);

        var file = Assert.Single(plan.Files);
        Assert.Equal("Vol.1", file.Label);
        Assert.Equal(6, file.Chapters.Count);
        Assert.Empty(file.NewChapters);
        Assert.Contains(file.Replaces, r => r.RelativePath.EndsWith("Berserk Vol.1 Ch.1.cbz"));
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
    public async Task Replace_imports_and_deletes_the_files_it_supersedes()
    {
        var (series, item) = SeedLibrary();
        SeedVolumeDownload(1, 2, 3, 4, 5, 6);

        var outcome = await Service().ImportAsync(
            item, series, _downloads, TorrentImportMode.Replace, CancellationToken.None);

        Assert.True(outcome.Applied);
        Assert.Equal(1, outcome.Imported);
        Assert.Equal(6, outcome.Deleted);

        Assert.True(File.Exists(Path.Combine(_root, "Berserk", "Berserk v01 (Digital) (1r0n).cbz")));
        Assert.False(File.Exists(Path.Combine(_root, "Berserk", "Berserk Vol.1 Ch.1.cbz")));

        using var db = _db.NewContext();
        var file = Assert.Single(db.ChapterFiles.Where(f => f.SeriesId == series.Id).ToList());
        Assert.All(db.Chapters.Where(c => c.SeriesId == series.Id).ToList(),
            c => Assert.Equal(file.Id, c.ChapterFileId));
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

    [Fact]
    public async Task A_download_that_is_gone_plans_as_an_error_rather_than_throwing()
    {
        var (series, item) = SeedLibrary();

        var plan = await Service().PlanAsync(
            item, series, Path.Combine(_root, "nope"), CancellationToken.None);

        Assert.NotNull(plan.Error);
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

        Assert.Equal("No comics found in the completed download (found 2 .pdf)", plan.Error);
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
