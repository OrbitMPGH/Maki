using System.IO.Compression;
using System.Text.Json;
using Maki.Api.Controllers;
using Maki.Api.Hubs;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Reading;
using Maki.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Byte-identical copies: which one is kept, deleting a spare, and moving chapters onto the
/// remaining copy when the one that backs them is deleted instead.
/// </summary>
public sealed class HealthDuplicateTests : IDisposable
{
    private readonly TestDb fixture = new();
    private readonly string root = Directory.CreateTempSubdirectory("maki-health-dupes-").FullName;
    public void Dispose() { fixture.Dispose(); Directory.Delete(root, true); }

    private HealthOperationService Operations(MakiDbContext db) => new(db, null!, null!,
        new ReaderArchiveCache(NullLogger<ReaderArchiveCache>.Instance),
        new EventBroadcaster(new NoopHubContext(), fixture.ScopeFactory()),
        new KavitaScanService(null!, null!, fixture.ScopeFactory(), NullLogger<KavitaScanService>.Instance),
        TestQuality.Create());

    private HealthController Controller(MakiDbContext db) =>
        new(db, null!, Operations(db), new HealthMatchService(db), new TestCurrentUser(1), null!, new TestLocalizer(), null!, null!, NullLogger<HealthController>.Instance)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    private sealed record Seeded(HealthFile Linked, HealthFile Spare, ChapterFile ChapterFile, List<Chapter> Chapters);

    /// <summary>A volume linked to two chapters, and a second copy of the same bytes that backs nothing.</summary>
    private async Task<Seeded> Seed(MakiDbContext db, string spareFolder = "Series")
    {
        var folder = new RootFolder { Path = root }; db.RootFolders.Add(folder); await db.SaveChangesAsync();
        var series = new Series { Title = "Series", SortTitle = "series", FolderName = "Series", RootFolderId = folder.Id };
        db.Series.Add(series); await db.SaveChangesAsync();
        Directory.CreateDirectory(Path.Combine(root, "Series"));
        Directory.CreateDirectory(Path.Combine(root, spareFolder));
        var linkedPath = Path.Combine("Series", "Series v10.cbz");
        var sparePath = Path.Combine(spareFolder, "Series Vol. 10.cbz");
        using (var zip = ZipFile.Open(Path.Combine(root, linkedPath), ZipArchiveMode.Create))
        { using var stream = zip.CreateEntry("ComicInfo.xml").Open(); stream.Write("<ComicInfo/>"u8); }
        File.Copy(Path.Combine(root, linkedPath), Path.Combine(root, sparePath));

        var cf = new ChapterFile { SeriesId = series.Id, RelativePath = linkedPath, Size = new FileInfo(Path.Combine(root, linkedPath)).Length };
        db.ChapterFiles.Add(cf); await db.SaveChangesAsync();
        var chapters = new List<Chapter>
        {
            new() { SeriesId = series.Id, ChapterFileId = cf.Id, Number = 77 },
            new() { SeriesId = series.Id, ChapterFileId = cf.Id, Number = 78 },
        };
        db.Chapters.AddRange(chapters); await db.SaveChangesAsync();

        var linked = new HealthFile { RootFolderId = folder.Id, RelativePath = linkedPath, ChapterFileId = cf.Id, SeriesId = series.Id };
        var spare = new HealthFile { RootFolderId = folder.Id, RelativePath = sparePath };
        db.HealthFiles.AddRange(linked, spare); await db.SaveChangesAsync();
        var scan = new HealthScanService(db);
        await scan.AnalyzeAsync(linked, root, true, default, 0, true);
        await scan.AnalyzeAsync(spare, root, true, default, 0, true);
        return new(linked, spare, cf, chapters);
    }

    [Fact]
    public async Task Both_copies_carry_the_finding_and_the_linked_one_is_kept()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db);

        Assert.True(db.HealthFindings.Any(f => f.FileId == s.Linked.Id && f.Kind == "duplicate" && f.State == "open"));
        Assert.True(db.HealthFindings.Any(f => f.FileId == s.Spare.Id && f.Kind == "duplicate" && f.State == "open"));
        var group = (await HealthDuplicates.GroupsAsync(db, db.HealthFiles.Select(f => f.ContentHash!), default)).Single().Value;
        Assert.Equal([s.Linked.Id, s.Spare.Id], group.Select(c => c.Id));
        Assert.True(group[0].Kept);
        Assert.True(group[1].Spare);
    }

    [Fact]
    public async Task Deleting_the_spare_leaves_the_chapters_alone_and_closes_both_findings()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db);

        var op = await Operations(db).DeleteNowAsync(s.Spare.Id, s.Spare.Version, 1, default);

        Assert.Equal("completed", op.Status);
        Assert.Null(op.RelinkFileId);
        Assert.False(File.Exists(Path.Combine(root, s.Spare.RelativePath)));
        Assert.True(File.Exists(Path.Combine(root, s.Linked.RelativePath)));
        Assert.All(db.Chapters.AsNoTracking(), c => Assert.Equal(s.ChapterFile.Id, c.ChapterFileId));
        // The copy left behind has no twin now, and nothing else would ever close its finding.
        Assert.False(db.HealthFindings.AsNoTracking().Any(f => f.Kind == "duplicate" && f.State != "resolved"));
    }

    [Fact]
    public async Task Deleting_the_linked_copy_moves_its_chapters_onto_the_spare()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db);
        var service = Operations(db);

        var preview = await service.PreviewDeleteAsync(s.Linked.Id, s.Linked.Version, 1, default);
        Assert.Equal(s.Spare.Id, preview.RelinkFileId);
        await service.ApplyAsync(preview.Id, s.Linked.Version, true, false, default);

        Assert.False(File.Exists(Path.Combine(root, s.Linked.RelativePath)));
        var cf = db.ChapterFiles.AsNoTracking().Single();
        Assert.Equal(s.ChapterFile.Id, cf.Id);
        Assert.Equal(s.Spare.RelativePath, cf.RelativePath);
        Assert.All(db.Chapters.AsNoTracking(), c => Assert.Equal(cf.Id, c.ChapterFileId));
        var spare = db.HealthFiles.AsNoTracking().Single(f => f.Id == s.Spare.Id);
        Assert.Equal(cf.Id, spare.ChapterFileId);
        Assert.False(db.HealthFindings.AsNoTracking().Any(f => f.FileId == s.Spare.Id && f.State != "resolved" && (f.Kind == "unlinked" || f.Kind == "duplicate")));
        Assert.Equal("health.history.deletedRelinked", db.HealthHistory.Single(h => h.FileId == s.Linked.Id).MessageKey);
    }

    [Fact]
    public async Task A_spare_that_changed_since_the_preview_refuses_the_delete()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db);
        var service = Operations(db);
        var preview = await service.PreviewDeleteAsync(s.Linked.Id, s.Linked.Version, 1, default);
        File.Delete(Path.Combine(root, s.Spare.RelativePath));

        var refused = await Assert.ThrowsAsync<HealthRefusedException>(() =>
            service.ApplyAsync(preview.Id, s.Linked.Version, true, false, default));

        Assert.Equal("error.health.relinkTargetChanged", refused.Key);
        Assert.True(File.Exists(Path.Combine(root, s.Linked.RelativePath)));
        Assert.All(db.Chapters.AsNoTracking(), c => Assert.Equal(s.ChapterFile.Id, c.ChapterFileId));
    }

    [Fact]
    public async Task A_copy_in_another_folder_is_never_a_relink_target()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db, spareFolder: "Elsewhere");

        var op = await Operations(db).DeleteNowAsync(s.Linked.Id, s.Linked.Version, 1, default);

        Assert.Null(op.RelinkFileId);
        Assert.Empty(db.ChapterFiles.AsNoTracking());
        Assert.All(db.Chapters.AsNoTracking(), c => Assert.Null(c.ChapterFileId));
    }

    [Fact]
    public async Task A_refused_delete_does_not_leave_an_operation_in_review()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db);
        // Same size and write time, different bytes: only the hash at apply can tell.
        var path = Path.Combine(root, s.Spare.RelativePath);
        var written = File.GetLastWriteTimeUtc(path);
        var bytes = await File.ReadAllBytesAsync(path);
        bytes[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(path, bytes);
        File.SetLastWriteTimeUtc(path, written);
        var service = Operations(db);

        await Assert.ThrowsAsync<HealthRefusedException>(() => service.DeleteNowAsync(s.Spare.Id, s.Spare.Version, 1, default));

        Assert.True(File.Exists(path));
        Assert.Equal("failed", db.HealthOperations.AsNoTracking().Single().Status);
        // A second attempt is refused for the bytes again, not blocked by the first one.
        var again = await Assert.ThrowsAsync<HealthRefusedException>(() => service.DeleteNowAsync(s.Spare.Id, s.Spare.Version, 1, default));
        Assert.Equal("error.health.fileContentChanged", again.Key);
    }

    [Fact]
    public async Task A_spare_given_chapters_since_the_list_was_read_is_refused()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db);
        var service = Operations(db);
        // Deleting the kept copy moves its chapters onto the spare without changing the spare's version.
        await service.DeleteNowAsync(s.Linked.Id, s.Linked.Version, 1, default);

        var refused = await Assert.ThrowsAsync<HealthRefusedException>(() =>
            service.DeleteNowAsync(s.Spare.Id, s.Spare.Version, 1, default, spare: true));

        Assert.Equal("error.health.spareInUse", refused.Key);
        Assert.True(File.Exists(Path.Combine(root, s.Spare.RelativePath)));
        Assert.All(db.Chapters.AsNoTracking(), c => Assert.Equal(s.ChapterFile.Id, c.ChapterFileId));
    }

    [Fact]
    public async Task A_spare_whose_only_twin_is_gone_from_disk_is_refused()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db);
        File.Delete(Path.Combine(root, s.Linked.RelativePath));

        var refused = await Assert.ThrowsAsync<HealthRefusedException>(() =>
            Operations(db).DeleteNowAsync(s.Spare.Id, s.Spare.Version, 1, default, spare: true));

        Assert.Equal("error.health.spareLastCopy", refused.Key);
        Assert.True(File.Exists(Path.Combine(root, s.Spare.RelativePath)));
        Assert.Equal("failed", db.HealthOperations.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task A_spare_that_is_the_kept_file_under_another_case_is_refused()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db);
        File.Delete(Path.Combine(root, s.Spare.RelativePath));
        await db.HealthFiles.Where(f => f.Id == s.Spare.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.Removed, true));
        // What a case-insensitive mount lists next to the record's own spelling.
        var aliasPath = Path.Combine("SERIES", "series V10.cbz");
        var alias = Path.Combine(root, aliasPath);
        if (!File.Exists(alias))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(alias)!);
            File.Copy(Path.Combine(root, s.Linked.RelativePath), alias);
        }
        var row = new HealthFile { RootFolderId = s.Linked.RootFolderId, RelativePath = aliasPath };
        db.HealthFiles.Add(row); await db.SaveChangesAsync();
        await new HealthScanService(db).AnalyzeAsync(row, root, true, default, 0, true);

        var refused = await Assert.ThrowsAsync<HealthRefusedException>(() =>
            Operations(db).DeleteNowAsync(row.Id, row.Version, 1, default, spare: true));

        Assert.Equal("error.health.spareLastCopy", refused.Key);
        Assert.True(File.Exists(Path.Combine(root, s.Linked.RelativePath)));
    }

    [Fact]
    public async Task The_duplicate_filter_lists_one_row_per_set_with_its_spares()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db);

        var result = await Controller(db).Files(kind: "duplicate", state: "open", ct: default);

        var body = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(1, body.GetProperty("total").GetInt32());
        var row = body.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(s.Linked.Id, row.GetProperty("Id").GetInt32());
        Assert.Equal(2, row.GetProperty("copies").GetArrayLength());
        var spares = body.GetProperty("spares");
        Assert.Equal(1, spares.GetProperty("total").GetInt32());
        Assert.Equal(s.Spare.Id, spares.GetProperty("items").EnumerateArray().Single().GetProperty("Id").GetInt32());
    }

    [Fact]
    public async Task File_detail_names_the_copies_and_where_the_chapters_would_go()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db);

        var result = await Controller(db).FileDetail(s.Linked.Id, default);

        var body = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
        var copies = body.GetProperty("copies").EnumerateArray().ToList();
        Assert.Equal(2, copies.Count);
        Assert.Equal(2, copies[0].GetProperty("Chapters").GetArrayLength());
        Assert.Equal(s.Spare.Id, body.GetProperty("relinkTo").GetProperty("Id").GetInt32());
    }
}
