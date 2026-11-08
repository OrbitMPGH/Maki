using System.IO.Compression;
using System.Text.Json;
using Maki.Api.Controllers;
using Maki.Api.Hubs;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Images;
using Maki.Core.Reading;
using Maki.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The per-kind answers on the Files tab: a missing file found again at a new path, a record whose
/// size no longer matches its file, and unlinked files sorted into what each one needs.
/// </summary>
public sealed class HealthFindingWorkflowTests : IDisposable
{
    private readonly TestDb fixture = new();
    private readonly string root = Directory.CreateTempSubdirectory("maki-health-flow-").FullName;
    public void Dispose() { fixture.Dispose(); Directory.Delete(root, true); }

    private HealthOperationService Operations(MakiDbContext db) => new(db, null!, null!,
        new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
        new EventBroadcaster(new NoopHubContext(), fixture.ScopeFactory()),
        new KavitaScanService(null!, null!, fixture.ScopeFactory(), NullLogger<KavitaScanService>.Instance),
        TestQuality.Create());

    private HealthController Controller(MakiDbContext db) =>
        new(db, null!, Operations(db), new HealthMatchService(db), new TestCurrentUser(1), null!, new TestLocalizer(), null!, null!, NullLogger<HealthController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    /// <summary>A three-page chapter; <paramref name="comicInfo"/> stands in for a tagger's rewrite.</summary>
    private void WriteChapter(string relative, string comicInfo = "<ComicInfo/>", int seed = 1)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        for (var i = 0; i < 3; i++)
        {
            using var stream = zip.CreateEntry($"{i:000}.png").Open();
            var image = new Raster(8, 8);
            for (var y = 0; y < 8; y++)
            for (var x = 0; x < 8; x++) image[x, y] = new Rgba((byte)(x * (i + seed)), (byte)(y * 7), 40);
            stream.Write(image.EncodePng());
        }
        using var info = zip.CreateEntry("ComicInfo.xml").Open();
        info.Write(System.Text.Encoding.UTF8.GetBytes(comicInfo));
    }

    private sealed record Library(RootFolder Root, Series Series, ChapterFile Record, Chapter Chapter, HealthFile File);

    private async Task<Library> SeedLinked(MakiDbContext db, string folder = "Series", string name = "Series Ch.1.cbz", bool verify = true)
    {
        var rootFolder = new RootFolder { Path = root }; db.RootFolders.Add(rootFolder); await db.SaveChangesAsync();
        var series = new Series { Title = "Series", SortTitle = "series", FolderName = folder, RootFolderId = rootFolder.Id };
        db.Series.Add(series); await db.SaveChangesAsync();
        var relative = Path.Combine(folder, name);
        WriteChapter(relative);
        var record = new ChapterFile { SeriesId = series.Id, RelativePath = relative, Size = new FileInfo(Path.Combine(root, relative)).Length, PageCount = 3 };
        db.ChapterFiles.Add(record); await db.SaveChangesAsync();
        var chapter = new Chapter { SeriesId = series.Id, ChapterFileId = record.Id, Number = 1, Wanted = true };
        db.Chapters.Add(chapter); await db.SaveChangesAsync();
        var file = new HealthFile { RootFolderId = rootFolder.Id, RelativePath = relative, ChapterFileId = record.Id, SeriesId = series.Id };
        db.HealthFiles.Add(file); await db.SaveChangesAsync();
        await new HealthScanService(db).AnalyzeAsync(file, root, true, default, 0, verify);
        return new(rootFolder, series, record, chapter, file);
    }

    /// <summary>The folder renamed outside Maki and ComicInfo rewritten on the way: the record now points nowhere.</summary>
    private async Task<HealthFile> MoveFolder(MakiDbContext db, Library lib, string to, string comicInfo = "<ComicInfo><Year>2024</Year></ComicInfo>", bool verify = true)
    {
        var name = Path.GetFileName(lib.File.RelativePath);
        File.Delete(Path.Combine(root, lib.File.RelativePath));
        Directory.Delete(Path.Combine(root, Path.GetDirectoryName(lib.File.RelativePath)!));
        var relative = Path.Combine(to, name);
        WriteChapter(relative, comicInfo);
        var scan = new HealthScanService(db);
        await scan.AnalyzeAsync(lib.File, root, true, default);
        var moved = new HealthFile { RootFolderId = lib.Root.Id, RelativePath = relative };
        db.HealthFiles.Add(moved); await db.SaveChangesAsync();
        await scan.AnalyzeAsync(moved, root, true, default, 0, verify);
        return moved;
    }

    [Fact]
    public async Task A_missing_file_is_not_also_reported_as_a_size_mismatch()
    {
        using var db = fixture.NewContext();
        var lib = await SeedLinked(db);
        File.Delete(Path.Combine(root, lib.File.RelativePath));

        await new HealthScanService(db).AnalyzeAsync(lib.File, root, true, default);

        var open = db.HealthFindings.AsNoTracking().Where(f => f.FileId == lib.File.Id && f.Version == lib.File.Version && f.State == "open").Select(f => f.Kind).ToList();
        Assert.Equal(["missing"], open);
    }

    [Fact]
    public async Task A_size_mismatch_left_open_on_a_missing_file_closes_on_the_next_pass()
    {
        using var db = fixture.NewContext();
        var lib = await SeedLinked(db);
        File.Delete(Path.Combine(root, lib.File.RelativePath));
        var scan = new HealthScanService(db);
        await scan.AnalyzeAsync(lib.File, root, true, default);
        // What an older scan left behind on the same version.
        db.HealthFindings.Add(new HealthFinding { FileId = lib.File.Id, Version = lib.File.Version, Kind = "sizeMismatch", Severity = "warning", MessageKey = "health.finding.sizeMismatch" });
        await db.SaveChangesAsync();

        await scan.AnalyzeAsync(lib.File, root, true, default);

        Assert.False(db.HealthFindings.AsNoTracking().Any(f => f.FileId == lib.File.Id && f.Kind == "sizeMismatch" && f.State == "open"));
    }

    [Fact]
    public async Task A_renamed_folder_is_found_by_its_pages_and_relinked_with_the_series()
    {
        using var db = fixture.NewContext();
        var lib = await SeedLinked(db);
        var moved = await MoveFolder(db, lib, "Series (2024)");

        var found = (await HealthMovedFiles.FindAsync(db, [lib.File.Id], default))[lib.File.Id];
        Assert.Equal(moved.Id, found.TargetId);
        Assert.Equal(HealthMovedFiles.SamePages, found.Evidence);

        await Operations(db).RelinkMovedAsync(lib.File.Id, lib.File.Version, moved.Id, moved.Version, false, 1, default);

        var record = db.ChapterFiles.AsNoTracking().Single();
        Assert.Equal(lib.Record.Id, record.Id);
        Assert.Equal(moved.RelativePath, record.RelativePath);
        Assert.Equal(moved.Size, record.Size);
        Assert.Equal(record.Id, db.Chapters.AsNoTracking().Single().ChapterFileId);
        Assert.True(db.HealthFiles.AsNoTracking().Single(f => f.Id == lib.File.Id).Removed);
        Assert.Equal(record.Id, db.HealthFiles.AsNoTracking().Single(f => f.Id == moved.Id).ChapterFileId);
        Assert.False(db.HealthFindings.AsNoTracking().Any(f => (f.FileId == lib.File.Id || f.Kind == "unlinked") && f.State == "open"));
        // The old folder is gone and nothing is left in it, so new downloads follow the files.
        Assert.Equal("Series (2024)", db.Series.AsNoTracking().Single().FolderName);
    }

    [Fact]
    public async Task An_indexed_copy_with_the_same_page_count_and_size_is_a_likely_match()
    {
        using var db = fixture.NewContext();
        var lib = await SeedLinked(db, verify: false);
        var moved = await MoveFolder(db, lib, "Series (2024)", verify: false);

        var found = (await HealthMovedFiles.FindAsync(db, [lib.File.Id], default))[lib.File.Id];

        Assert.Equal(moved.Id, found.TargetId);
        Assert.Equal(HealthMovedFiles.Likely, found.Evidence);
    }

    [Fact]
    public async Task A_name_only_match_is_refused_in_bulk_and_allowed_one_at_a_time()
    {
        using var db = fixture.NewContext();
        var lib = await SeedLinked(db);
        File.Delete(Path.Combine(root, lib.File.RelativePath));
        var scan = new HealthScanService(db);
        await scan.AnalyzeAsync(lib.File, root, true, default);
        // Same name, different chapter entirely: other pages, and a page count the record disagrees with.
        var relative = Path.Combine("Elsewhere", Path.GetFileName(lib.File.RelativePath));
        Directory.CreateDirectory(Path.Combine(root, "Elsewhere"));
        using (var zip = ZipFile.Open(Path.Combine(root, relative), ZipArchiveMode.Create))
        { using var s = zip.CreateEntry("001.png").Open(); s.Write(new Raster(4, 4).EncodePng()); }
        var other = new HealthFile { RootFolderId = lib.Root.Id, RelativePath = relative };
        db.HealthFiles.Add(other); await db.SaveChangesAsync();
        await scan.AnalyzeAsync(other, root, true, default, 0, true);
        var service = Operations(db);

        var refused = await Assert.ThrowsAsync<HealthRefusedException>(() =>
            service.RelinkMovedAsync(lib.File.Id, lib.File.Version, other.Id, other.Version, false, 1, default));
        Assert.Equal("error.health.relinkWeakMatch", refused.Key);
        Assert.Equal(lib.File.RelativePath, db.ChapterFiles.AsNoTracking().Single().RelativePath);

        await service.RelinkMovedAsync(lib.File.Id, lib.File.Version, other.Id, other.Version, true, 1, default);
        Assert.Equal(relative, db.ChapterFiles.AsNoTracking().Single().RelativePath);
        // The series did not move: "Elsewhere" is just where one file was found.
        Assert.Equal("Series", db.Series.AsNoTracking().Single().FolderName);
    }

    [Fact]
    public async Task A_copy_inside_another_series_folder_is_never_offered()
    {
        using var db = fixture.NewContext();
        var lib = await SeedLinked(db);
        db.Series.Add(new Series { Title = "Other", SortTitle = "other", FolderName = "Other", RootFolderId = lib.Root.Id });
        await db.SaveChangesAsync();
        await MoveFolder(db, lib, "Other");

        Assert.Empty(await HealthMovedFiles.FindAsync(db, [lib.File.Id], default));
    }

    [Fact]
    public async Task Updating_a_record_takes_the_size_on_disk_and_queues_a_scan()
    {
        using var db = fixture.NewContext();
        var lib = await SeedLinked(db);
        var record = db.ChapterFiles.Single();
        record.Size += 250;
        record.MeasuredAtUtc = DateTime.UtcNow;
        record.ConversionSavedBytes = 10;
        await db.SaveChangesAsync();

        var updated = await Operations(db).RefreshRecordsAsync([lib.File.Id], 1, default);

        Assert.Equal(1, updated);
        var after = db.ChapterFiles.AsNoTracking().Single();
        Assert.Equal(new FileInfo(Path.Combine(root, lib.File.RelativePath)).Length, after.Size);
        Assert.Null(after.MeasuredAtUtc);
        Assert.Equal(0, after.ConversionSavedBytes);
        Assert.Contains(db.HealthScans.AsNoTracking(), s => s.FileIdsJson.Contains(lib.File.Id.ToString()));
    }

    [Fact]
    public async Task Getting_a_new_copy_verifies_first_and_requests_nothing_when_the_file_reads_clean()
    {
        using var db = fixture.NewContext();
        var lib = await SeedLinked(db, verify: false);
        // What an index pass can only guess at and a full read clears.
        db.HealthFindings.Add(new HealthFinding { FileId = lib.File.Id, Version = lib.File.Version, Kind = "damagedImage", Severity = "error", MessageKey = "health.finding.cannotRead" });
        await db.SaveChangesAsync();

        var result = await Controller(db).RepairAuto(new HealthController.FileReview(lib.File.Id, lib.File.Version), new HealthScanService(db), default);

        var body = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal("clean", body.GetProperty("state").GetString());
        Assert.Equal(ArchiveHealthAnalyzer.VerifyVersion, db.HealthFiles.AsNoTracking().Single().VerifiedVersion);
        Assert.Empty(db.HealthOperations);
    }

    [Fact]
    public async Task Bulk_replacements_queue_one_verify_for_files_only_indexed_so_far()
    {
        using var db = fixture.NewContext();
        var lib = await SeedLinked(db, verify: false);

        var result = await Controller(db).RepairBulk(new HealthController.BulkRepair([new(lib.File.Id, lib.File.Version)]), default);

        var body = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(0, body.GetProperty("requested").GetInt32());
        Assert.Equal(1, body.GetProperty("verifying").GetInt32());
        var scan = db.HealthScans.AsNoTracking().Single();
        Assert.True(scan.Verify);
        Assert.Empty(db.HealthOperations);
    }

    [Fact]
    public async Task Unlinked_files_are_sorted_into_what_each_one_needs()
    {
        using var db = fixture.NewContext();
        var lib = await SeedLinked(db);
        db.Chapters.Add(new Chapter { SeriesId = lib.Series.Id, Number = 2 });
        await db.SaveChangesAsync();
        var scan = new HealthScanService(db);
        async Task<HealthFile> Add(string relative, int seed)
        {
            WriteChapter(relative, seed: seed);
            var file = new HealthFile { RootFolderId = lib.Root.Id, RelativePath = relative };
            db.HealthFiles.Add(file); await db.SaveChangesAsync();
            await scan.AnalyzeAsync(file, root, true, default, 0, true);
            return file;
        }
        var import = await Add(Path.Combine("Series", "Series Ch.2.cbz"), 2);
        var rival = await Add(Path.Combine("Series", "Series Ch.1 [other].cbz"), 3);
        var noChapter = await Add(Path.Combine("Series", "Series Ch.9.cbz"), 4);
        var outside = await Add(Path.Combine("Loose", "Loose Ch.1.cbz"), 5);

        var result = await Controller(db).Files(kind: "unlinked", ct: default);

        var body = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
        var cases = body.GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("Id").GetInt32(), i => i.GetProperty("detail").GetProperty("Case").GetString());
        Assert.Equal(HealthMatchService.Cases.Import, cases[import.Id]);
        Assert.Equal(HealthMatchService.Cases.Rival, cases[rival.Id]);
        Assert.Equal(HealthMatchService.Cases.NoChapter, cases[noChapter.Id]);
        Assert.Equal(HealthMatchService.Cases.Outside, cases[outside.Id]);
        var importable = body.GetProperty("cases").GetProperty("importable");
        Assert.Equal(import.Id, importable.GetProperty("items").EnumerateArray().Single().GetProperty("Id").GetInt32());
        Assert.Equal("Loose", body.GetProperty("cases").GetProperty("outside").EnumerateArray().Single().GetProperty("folder").GetString());

        var filtered = await Controller(db).Files(kind: "unlinked", fileCase: HealthMatchService.Cases.Rival, ct: default);
        var rows = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(filtered).Value).GetProperty("items");
        Assert.Equal(rival.Id, rows.EnumerateArray().Single().GetProperty("Id").GetInt32());
    }

    [Fact]
    public async Task The_missing_filter_offers_moved_files_for_one_relink_and_groups_the_rest_by_folder()
    {
        using var db = fixture.NewContext();
        var lib = await SeedLinked(db);
        var moved = await MoveFolder(db, lib, "Series (2024)");

        var result = await Controller(db).Files(kind: "missing", ct: default);

        var moves = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value).GetProperty("moves");
        Assert.Equal(1, moves.GetProperty("total").GetInt32());
        var item = moves.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(moved.Id, item.GetProperty("TargetId").GetInt32());
        Assert.Empty(moves.GetProperty("gone").EnumerateArray());
    }

    [Fact]
    public async Task A_file_two_missing_records_could_claim_is_offered_to_neither_and_is_not_gone()
    {
        using var db = fixture.NewContext();
        var lib = await SeedLinked(db);
        await MoveFolder(db, lib, "Series (2024)");
        var other = new ChapterFile { SeriesId = lib.Series.Id, RelativePath = Path.Combine("Series", "Old", "Series Ch.1.cbz") };
        db.ChapterFiles.Add(other); await db.SaveChangesAsync();
        db.HealthFiles.Add(new HealthFile { RootFolderId = lib.Root.Id, RelativePath = other.RelativePath, ChapterFileId = other.Id, SeriesId = lib.Series.Id, Size = -1 });
        await db.SaveChangesAsync();

        var ambiguous = new HashSet<int>();
        Assert.Empty(await HealthMovedFiles.FindAsync(db, [lib.File.Id], default, ambiguous));
        Assert.Equal([lib.File.Id], ambiguous);

        var result = await Controller(db).Files(kind: "missing", search: "Ch.1", ct: default);
        var moves = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value).GetProperty("moves");
        Assert.Equal(0, moves.GetProperty("total").GetInt32());
        Assert.Empty(moves.GetProperty("gone").EnumerateArray());
    }

    [Fact]
    public async Task A_folder_renamed_only_in_case_is_followed()
    {
        using var db = fixture.NewContext();
        var lib = await SeedLinked(db, folder: "series");
        var oldPath = lib.Record.RelativePath;
        Directory.Move(Path.Combine(root, "series"), Path.Combine(root, "Series-tmp"));
        Directory.Move(Path.Combine(root, "Series-tmp"), Path.Combine(root, "Series"));
        lib.Record.RelativePath = Path.Combine("Series", Path.GetFileName(oldPath));
        await db.SaveChangesAsync();

        await Operations(db).FollowFolderAsync(lib.Record, oldPath, lib.Root, 1, default);

        Assert.Equal("Series", db.Series.AsNoTracking().Single().FolderName);
    }
}
