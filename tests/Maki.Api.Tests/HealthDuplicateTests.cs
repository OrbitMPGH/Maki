using System.IO.Compression;
using System.Text.Json;
using Maki.Api.Controllers;
using Maki.Api.Hubs;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Io;
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
    public async Task Nothing_is_a_spare_while_every_kept_copy_is_missing()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db);
        await db.HealthFiles.Where(f => f.Id == s.Linked.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.Size, -1L));

        var group = (await HealthDuplicates.GroupsAsync(db, db.HealthFiles.Select(f => f.ContentHash!), default)).Single().Value;

        Assert.True(group.Single(c => c.Id == s.Linked.Id).Kept);
        Assert.DoesNotContain(group, c => c.Spare);
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
    public async Task A_record_stored_with_the_other_separator_still_moves_with_the_delete()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db);
        var stored = s.ChapterFile.RelativePath;
        var other = stored.Contains('/') ? stored.Replace('/', '\\') : stored.Replace('\\', '/');
        await db.ChapterFiles.Where(f => f.Id == s.ChapterFile.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.RelativePath, other));

        var op = await Operations(db).DeleteNowAsync(s.Linked.Id, s.Linked.Version, 1, default);

        Assert.Equal(s.Spare.Id, op.RelinkFileId);
        Assert.Equal(s.Spare.RelativePath, db.ChapterFiles.AsNoTracking().Single().RelativePath);
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
    public async Task A_file_that_cannot_be_read_at_apply_fails_the_delete_instead_of_blocking_the_file()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db);
        var path = Path.Combine(root, s.Spare.RelativePath);
        var service = Operations(db);
        FileStream? held = null;
        if (OperatingSystem.IsWindows()) held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        else File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            try { await File.ReadAllBytesAsync(path); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            await Assert.ThrowsAnyAsync<Exception>(() => service.DeleteNowAsync(s.Spare.Id, s.Spare.Version, 1, default));
        }
        finally
        {
            held?.Dispose();
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var op = db.HealthOperations.AsNoTracking().Single();
        Assert.Equal("failed", op.Status);
        Assert.Equal("health.operation.error.deleteFailed", op.ErrorKey);
        Assert.True(File.Exists(path));
        Assert.Equal("completed", (await service.DeleteNowAsync(s.Spare.Id, s.Spare.Version, 1, default)).Status);
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
    public async Task A_spare_that_is_the_kept_file_under_another_case_is_refused_and_a_real_second_file_is_not()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db);
        File.Delete(Path.Combine(root, s.Spare.RelativePath));
        await db.HealthFiles.Where(f => f.Id == s.Spare.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.Removed, true));
        // What a case-insensitive mount lists next to the record's own spelling.
        var aliasPath = Path.Combine("SERIES", "series V10.cbz");
        var alias = Path.Combine(root, aliasPath);
        var secondFile = !File.Exists(alias);
        if (secondFile)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(alias)!);
            File.Copy(Path.Combine(root, s.Linked.RelativePath), alias);
        }
        var row = new HealthFile { RootFolderId = s.Linked.RootFolderId, RelativePath = aliasPath };
        db.HealthFiles.Add(row); await db.SaveChangesAsync();
        await new HealthScanService(db).AnalyzeAsync(row, root, true, default, 0, true);

        if (secondFile)
        {
            // A case-sensitive filesystem holds two files, and the second really is a spare.
            Assert.Equal("completed", (await Operations(db).DeleteNowAsync(row.Id, row.Version, 1, default, spare: true)).Status);
        }
        else
        {
            var refused = await Assert.ThrowsAsync<HealthRefusedException>(() =>
                Operations(db).DeleteNowAsync(row.Id, row.Version, 1, default, spare: true));
            // Windows matches the record's path ignoring case and finds its chapters; a case-insensitive
            // mount elsewhere finds the kept copy is the same file.
            Assert.Contains(refused.Key, new[] { "error.health.spareInUse", "error.health.spareNotDistinct" });
        }
        Assert.True(File.Exists(Path.Combine(root, s.Linked.RelativePath)));
    }

    [Fact]
    public async Task A_spare_whose_twin_cannot_be_told_apart_from_it_is_refused()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db);
        var service = Operations(db);
        service.SameFile = (_, _) => FileSameness.Unknown;

        var refused = await Assert.ThrowsAsync<HealthRefusedException>(() =>
            service.DeleteNowAsync(s.Spare.Id, s.Spare.Version, 1, default, spare: true));

        Assert.Equal("error.health.spareNotDistinct", refused.Key);
        Assert.True(File.Exists(Path.Combine(root, s.Spare.RelativePath)));
    }

    [Fact]
    public async Task A_spare_in_a_second_root_naming_the_same_folder_is_refused()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db);
        File.Delete(Path.Combine(root, s.Spare.RelativePath));
        await db.HealthFiles.Where(f => f.Id == s.Spare.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.Removed, true));
        var link = root + "-link";
        if (!MakeDirectoryLink(link, root)) return;
        try
        {
            var second = new RootFolder { Path = link }; db.RootFolders.Add(second); await db.SaveChangesAsync();
            var row = new HealthFile { RootFolderId = second.Id, RelativePath = s.Linked.RelativePath };
            db.HealthFiles.Add(row); await db.SaveChangesAsync();
            await new HealthScanService(db).AnalyzeAsync(row, link, true, default, 0, true);

            var refused = await Assert.ThrowsAsync<HealthRefusedException>(() =>
                Operations(db).DeleteNowAsync(row.Id, row.Version, 1, default, spare: true));

            Assert.Equal("error.health.spareNotDistinct", refused.Key);
            Assert.True(File.Exists(Path.Combine(root, s.Linked.RelativePath)));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>A second root holding a byte-identical copy of the linked file, as an unlinked inventory row.</summary>
    private async Task<HealthFile> SecondRootCopy(MakiDbContext db, Seeded s, string secondRoot, bool copy)
    {
        if (copy)
        {
            Directory.CreateDirectory(Path.Combine(secondRoot, "Series"));
            File.Copy(Path.Combine(root, s.Linked.RelativePath), Path.Combine(secondRoot, s.Linked.RelativePath));
        }
        var second = new RootFolder { Path = secondRoot }; db.RootFolders.Add(second); await db.SaveChangesAsync();
        var row = new HealthFile { RootFolderId = second.Id, RelativePath = s.Linked.RelativePath };
        db.HealthFiles.Add(row); await db.SaveChangesAsync();
        await new HealthScanService(db).AnalyzeAsync(row, secondRoot, true, default, 0, true);
        return row;
    }

    [Fact]
    public async Task A_second_root_that_is_the_first_under_another_name_is_caught_by_the_probe()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db);
        var link = root + "-probe-link";
        if (!MakeDirectoryLink(link, root)) return;
        try
        {
            var row = await SecondRootCopy(db, s, link, copy: false);
            var service = Operations(db);
            // Two mounts of one share: file identity alone reads the two as separate files.
            service.SameFile = (_, _) => FileSameness.Different;

            var refused = await Assert.ThrowsAsync<HealthRefusedException>(() =>
                service.DeleteNowAsync(row.Id, row.Version, 1, default, spare: true));

            Assert.Equal("error.health.spareNotDistinct", refused.Key);
            Assert.True(File.Exists(Path.Combine(root, s.Linked.RelativePath)));
            Assert.False(Directory.Exists(Path.Combine(root, ".maki")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task A_second_root_that_is_a_folder_of_the_first_is_caught_by_the_probe_either_way_round()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db);
        var link = root + "-sub-link";
        if (!MakeDirectoryLink(link, Path.Combine(root, "Series"))) return;
        try
        {
            var second = new RootFolder { Path = link }; db.RootFolders.Add(second); await db.SaveChangesAsync();
            var scan = new HealthScanService(db);
            var linkedThere = new HealthFile { RootFolderId = second.Id, RelativePath = Path.GetFileName(s.Linked.RelativePath) };
            var spareThere = new HealthFile { RootFolderId = second.Id, RelativePath = Path.GetFileName(s.Spare.RelativePath) };
            db.HealthFiles.AddRange(linkedThere, spareThere); await db.SaveChangesAsync();
            await scan.AnalyzeAsync(linkedThere, link, true, default, 0, true);
            await scan.AnalyzeAsync(spareThere, link, true, default, 0, true);
            // Two mounts of one share: file identity alone reads every pair as separate files.
            FileSameness Apart(string a, string b) => FileSameness.Different;

            // The spare's root is the folder: only the kept copy in the first root is left besides it.
            await db.HealthFiles.Where(f => f.Id == s.Spare.Id || f.Id == spareThere.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.Removed, true));
            var service = Operations(db);
            service.SameFile = Apart;
            var refused = await Assert.ThrowsAsync<HealthRefusedException>(() =>
                service.DeleteNowAsync(linkedThere.Id, linkedThere.Version, 1, default, spare: true));
            Assert.Equal("error.health.spareNotDistinct", refused.Key);

            // The copy's root is the folder: the spare in the first root has only its twin under the link.
            await db.HealthFiles.Where(f => f.Id == spareThere.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.Removed, false));
            await db.HealthFiles.Where(f => f.Id == s.Linked.Id || f.Id == linkedThere.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.Removed, true));
            await db.HealthFiles.Where(f => f.Id == s.Spare.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.Removed, false));
            service = Operations(db);
            service.SameFile = Apart;
            refused = await Assert.ThrowsAsync<HealthRefusedException>(() =>
                service.DeleteNowAsync(s.Spare.Id, s.Spare.Version, 1, default, spare: true));
            Assert.Equal("error.health.spareNotDistinct", refused.Key);

            Assert.True(File.Exists(Path.Combine(root, s.Linked.RelativePath)));
            Assert.True(File.Exists(Path.Combine(root, s.Spare.RelativePath)));
            Assert.False(Directory.Exists(Path.Combine(root, "Series", ".maki")));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task A_spare_in_a_separate_root_is_deleted_and_one_whose_probe_fails_is_refused()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db);
        var separate = Directory.CreateTempSubdirectory("maki-health-second-").FullName;
        var blocked = Directory.CreateTempSubdirectory("maki-health-blocked-").FullName;
        try
        {
            var row = await SecondRootCopy(db, s, separate, copy: true);
            Assert.Equal("completed", (await Operations(db).DeleteNowAsync(row.Id, row.Version, 1, default, spare: true)).Status);
            Assert.False(File.Exists(Path.Combine(separate, s.Linked.RelativePath)));

            // A file where the probe's folder should be: nothing can be written to check with.
            File.WriteAllText(Path.Combine(blocked, ".maki"), "");
            var stuck = await SecondRootCopy(db, s, blocked, copy: true);
            var refused = await Assert.ThrowsAsync<HealthRefusedException>(() =>
                Operations(db).DeleteNowAsync(stuck.Id, stuck.Version, 1, default, spare: true));
            Assert.Equal("error.health.spareNotDistinct", refused.Key);
            Assert.True(File.Exists(Path.Combine(blocked, s.Linked.RelativePath)));
        }
        finally
        {
            Directory.Delete(separate, true);
            Directory.Delete(blocked, true);
        }
    }

    [Fact]
    public async Task A_plain_delete_of_a_file_a_linked_twin_may_be_is_refused()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db);
        var service = Operations(db);
        // What a case-insensitive mount or an invented inode makes of the spare and the linked copy.
        service.SameFile = (_, _) => FileSameness.Same;

        var refused = await Assert.ThrowsAsync<HealthRefusedException>(() =>
            service.DeleteNowAsync(s.Spare.Id, s.Spare.Version, 1, default));

        Assert.Equal("error.health.fileBacksChapters", refused.Key);
        Assert.True(File.Exists(Path.Combine(root, s.Spare.RelativePath)));
        Assert.Equal("failed", db.HealthOperations.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task A_plain_delete_is_refused_while_a_linked_twin_reads_as_missing()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db);
        var service = Operations(db);
        // Gone at the path it was recorded at: an empty mount point looks the same.
        var linkedPath = Path.Combine(root, s.Linked.RelativePath);
        var bytes = await File.ReadAllBytesAsync(linkedPath);
        File.Delete(linkedPath);

        var refused = await Assert.ThrowsAsync<HealthRefusedException>(() =>
            service.DeleteNowAsync(s.Spare.Id, s.Spare.Version, 1, default));
        Assert.Equal("error.health.fileBacksChapters", refused.Key);

        // Recorded missing by a scan, with the hash dropped: only an earlier version still has it.
        await File.WriteAllBytesAsync(linkedPath, bytes);
        Assert.True(db.HealthFileVersions.Any(v => v.FileId == s.Linked.Id && v.ContentHash == s.Linked.ContentHash));
        await db.HealthFiles.Where(f => f.Id == s.Linked.Id).ExecuteUpdateAsync(u => u
            .SetProperty(f => f.Size, -1L).SetProperty(f => f.ContentHash, (string?)null));
        refused = await Assert.ThrowsAsync<HealthRefusedException>(() =>
            service.DeleteNowAsync(s.Spare.Id, s.Spare.Version, 1, default));
        Assert.Equal("error.health.fileBacksChapters", refused.Key);
        Assert.True(File.Exists(Path.Combine(root, s.Spare.RelativePath)));
    }

    [Fact]
    public async Task An_unverified_linked_file_with_the_same_name_and_size_that_may_be_this_one_refuses_the_delete()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db);
        await db.HealthFiles.Where(f => f.Id == s.Linked.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.Removed, true));
        var series = db.Series.AsNoTracking().Single();
        var otherPath = Path.Combine("Other", Path.GetFileName(s.Spare.RelativePath).ToUpperInvariant());
        Directory.CreateDirectory(Path.Combine(root, "Other"));
        File.Copy(Path.Combine(root, s.Spare.RelativePath), Path.Combine(root, otherPath));
        var record = new ChapterFile { SeriesId = series.Id, RelativePath = otherPath, Size = s.Spare.Size };
        db.ChapterFiles.Add(record); await db.SaveChangesAsync();
        db.HealthFiles.Add(new HealthFile
        {
            RootFolderId = s.Spare.RootFolderId, RelativePath = otherPath, ChapterFileId = record.Id, SeriesId = series.Id, Size = s.Spare.Size,
        });
        await db.SaveChangesAsync();
        var service = Operations(db);
        service.SameFile = (_, _) => FileSameness.Unknown;

        var refused = await Assert.ThrowsAsync<HealthRefusedException>(() =>
            service.DeleteNowAsync(s.Spare.Id, s.Spare.Version, 1, default));

        Assert.Equal("error.health.fileBacksChapters", refused.Key);
        Assert.True(File.Exists(Path.Combine(root, s.Spare.RelativePath)));
    }

    [Fact]
    public async Task A_plain_delete_through_a_second_root_naming_the_first_is_refused()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db);
        var link = root + "-plain-link";
        if (!MakeDirectoryLink(link, root)) return;
        try
        {
            var row = await SecondRootCopy(db, s, link, copy: false);
            var service = Operations(db);
            // Two mounts of one share: file identity alone reads the two as separate files.
            service.SameFile = (_, _) => FileSameness.Different;

            var refused = await Assert.ThrowsAsync<HealthRefusedException>(() =>
                service.DeleteNowAsync(row.Id, row.Version, 1, default));

            Assert.Equal("error.health.fileBacksChapters", refused.Key);
            Assert.True(File.Exists(Path.Combine(root, s.Linked.RelativePath)));
            Assert.All(db.Chapters.AsNoTracking(), c => Assert.Equal(s.ChapterFile.Id, c.ChapterFileId));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public async Task A_plain_delete_of_a_copy_whose_linked_twin_is_in_a_separate_root_goes_ahead()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db);
        var separate = Directory.CreateTempSubdirectory("maki-health-plain-").FullName;
        try
        {
            var row = await SecondRootCopy(db, s, separate, copy: true);

            Assert.Equal("completed", (await Operations(db).DeleteNowAsync(row.Id, row.Version, 1, default)).Status);
            Assert.False(File.Exists(Path.Combine(separate, s.Linked.RelativePath)));
            Assert.True(File.Exists(Path.Combine(root, s.Linked.RelativePath)));
        }
        finally
        {
            Directory.Delete(separate, true);
        }
    }

    [Fact]
    public async Task A_name_repeated_in_another_series_folder_does_not_block_a_delete_where_identity_is_unknown()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db);
        var series = db.Series.AsNoTracking().Single();
        void Write(string relative, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, relative))!);
            using var zip = ZipFile.Open(Path.Combine(root, relative), ZipArchiveMode.Create);
            using var stream = zip.CreateEntry("ComicInfo.xml").Open();
            stream.Write(System.Text.Encoding.UTF8.GetBytes(text));
        }
        var mine = Path.Combine("Series", "Chapter 001.cbz");
        var theirs = Path.Combine("Other", "Chapter 001.cbz");
        Write(mine, "<ComicInfo><Title>mine</Title></ComicInfo>");
        Write(theirs, "<ComicInfo><Title>theirs</Title></ComicInfo>");
        var record = new ChapterFile { SeriesId = series.Id, RelativePath = theirs, Size = new FileInfo(Path.Combine(root, theirs)).Length };
        db.ChapterFiles.Add(record); await db.SaveChangesAsync();
        var row = new HealthFile { RootFolderId = s.Linked.RootFolderId, RelativePath = mine };
        var linked = new HealthFile { RootFolderId = s.Linked.RootFolderId, RelativePath = theirs, ChapterFileId = record.Id, SeriesId = series.Id };
        db.HealthFiles.AddRange(row, linked); await db.SaveChangesAsync();
        var scan = new HealthScanService(db);
        await scan.AnalyzeAsync(row, root, true, default, 0, true);
        await scan.AnalyzeAsync(linked, root, true, default, 0, true);
        var service = Operations(db);
        service.SameFile = (_, _) => FileSameness.Unknown;

        Assert.Equal("completed", (await service.DeleteNowAsync(row.Id, row.Version, 1, default)).Status);
        Assert.True(File.Exists(Path.Combine(root, theirs)));
    }

    /// <summary>A junction on Windows (no privilege needed), a symbolic link elsewhere. False when the host refuses.</summary>
    private static bool MakeDirectoryLink(string link, string target)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(link, target);
                return true;
            }

            using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                ArgumentList = { "/c", "mklink", "/J", link, target },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            mklink.WaitForExit();
            return mklink.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
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
    public async Task The_duplicate_filter_offers_only_spares_inside_the_search()
    {
        using var db = fixture.NewContext();
        var s = await Seed(db);

        var kept = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(
            await Controller(db).Files(kind: "duplicate", search: "v10", ct: default)).Value);
        var spare = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(
            await Controller(db).Files(kind: "duplicate", search: "Vol.", ct: default)).Value);

        Assert.Equal(0, kept.GetProperty("spares").GetProperty("total").GetInt32());
        Assert.Equal(1, kept.GetProperty("items").EnumerateArray().Single().GetProperty("copies").GetArrayLength());
        Assert.Equal(s.Spare.Id, spare.GetProperty("spares").GetProperty("items").EnumerateArray().Single().GetProperty("Id").GetInt32());
        var row = spare.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(s.Spare.Id, row.GetProperty("copies").EnumerateArray().Single().GetProperty("Id").GetInt32());
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
