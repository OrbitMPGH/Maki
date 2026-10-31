using Maki.Api.Auth;
using Maki.Api.Controllers;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Maki.Api.Tests;

/// <summary>Validation and conflict handling in <see cref="RootFolderController"/>.</summary>
public class RootFolderControllerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        _db.Dispose();
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir); } catch { /* best-effort */ }
        }
    }

    private string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "maki-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private readonly UserSnapshotCache _snapshots = new(new MemoryCache(new MemoryCacheOptions()));

    private RootFolderController Controller(ICurrentUser? user = null) =>
        new(new TestLocalizer(), _db.NewContext(), _snapshots, user ?? new TestCurrentUser(1));

    private int SeedFolder()
    {
        using var db = _db.NewContext();
        var folder = new RootFolder { Path = TempDir() };
        db.RootFolders.Add(folder);
        db.SaveChanges();
        return folder.Id;
    }

    private static int[] ListedIds(IActionResult result) =>
        ((IEnumerable<object>)((OkObjectResult)result).Value!)
            .Select(dto => (int)dto.GetType().GetProperty("Id")!.GetValue(dto)!)
            .Order()
            .ToArray();

    [Fact]
    public async Task List_shows_every_folder_to_an_admin_without_a_blanket_grant()
    {
        var first = SeedFolder();
        var second = SeedFolder();
        var admin = new TestCurrentUser(1, permissions: MakiPermission.Admin, allRootFolders: false);

        var result = await Controller(admin).List(CancellationToken.None);

        Assert.Equal(new[] { first, second }.Order().ToArray(), ListedIds(result));
    }

    [Fact]
    public async Task List_shows_a_non_admin_only_the_folders_they_were_granted()
    {
        var granted = SeedFolder();
        SeedFolder();
        var user = new TestCurrentUser(
            2, permissions: MakiPermission.AddSeries, allRootFolders: false, rootFolderIds: new HashSet<int> { granted });

        var result = await Controller(user).List(CancellationToken.None);

        Assert.Equal([granted], ListedIds(result));
    }

    [Fact]
    public async Task List_is_empty_for_a_non_admin_with_no_grants()
    {
        SeedFolder();
        var user = new TestCurrentUser(2, permissions: MakiPermission.AddSeries, allRootFolders: false);

        var result = await Controller(user).List(CancellationToken.None);

        Assert.Empty(ListedIds(result));
    }

    [Fact]
    public void Only_the_list_is_open_to_AddSeries_and_the_writes_stay_admin()
    {
        static string? PolicyOf(string method) =>
            typeof(RootFolderController).GetMethod(method)!
                .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
                .Cast<AuthorizeAttribute>()
                .Single()
                .Policy;

        Assert.Null(typeof(RootFolderController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false).SingleOrDefault());
        Assert.Equal(Policies.AddSeries, PolicyOf(nameof(RootFolderController.List)));
        Assert.Equal(Policies.Admin, PolicyOf(nameof(RootFolderController.Add)));
        Assert.Equal(Policies.Admin, PolicyOf(nameof(RootFolderController.Delete)));
        Assert.Equal(Policies.Admin, PolicyOf(nameof(RootFolderController.Browse)));
    }

    [Fact]
    public void Browse_lists_subfolders_sorted_and_names_the_parent()
    {
        var dir = TempDir();
        var beta = Directory.CreateDirectory(Path.Combine(dir, "beta")).FullName;
        var alpha = Directory.CreateDirectory(Path.Combine(dir, "Alpha")).FullName;
        _tempDirs.Insert(0, beta);
        _tempDirs.Insert(0, alpha);
        File.WriteAllText(Path.Combine(dir, "not-a-folder.txt"), "");

        var listing = (RootFolderController.FolderListing)((OkObjectResult)Controller().Browse(dir)).Value!;

        Assert.Equal(Path.GetFullPath(dir), listing.Path);
        Assert.Equal(Directory.GetParent(dir)!.FullName, listing.Parent);
        Assert.Equal(["Alpha", "beta"], listing.Folders.Select(f => f.Name));
        Assert.Equal(alpha, listing.Folders[0].Path);
        File.Delete(Path.Combine(dir, "not-a-folder.txt"));
    }

    [Fact]
    public void Browse_rejects_a_missing_folder()
    {
        var missing = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid());
        Assert.IsType<BadRequestObjectResult>(Controller().Browse(missing));
    }

    [Fact]
    public void Browse_without_a_path_starts_at_the_top()
    {
        var listing = (RootFolderController.FolderListing)((OkObjectResult)Controller().Browse(null)).Value!;

        Assert.NotEmpty(listing.Folders);
        Assert.Null(listing.Parent);
    }

    [Fact]
    public async Task Add_rejects_a_blank_path()
    {
        var result = await Controller().Add(new RootFolder { Path = "  " }, CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Add_rejects_a_nonexistent_folder()
    {
        var result = await Controller().Add(
            new RootFolder { Path = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid()) },
            CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task Add_persists_a_valid_folder()
    {
        var dir = TempDir();

        var result = await Controller().Add(new RootFolder { Path = dir }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        using var db = _db.NewContext();
        Assert.Equal(1, db.RootFolders.Count(f => f.Path == dir));
    }

    [Fact]
    public async Task Add_rejects_a_duplicate()
    {
        var dir = TempDir();
        await Controller().Add(new RootFolder { Path = dir }, CancellationToken.None);

        var result = await Controller().Add(new RootFolder { Path = dir }, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task Delete_missing_returns_not_found()
    {
        Assert.IsType<NotFoundResult>(await Controller().Delete(999, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_rejects_a_folder_in_use()
    {
        var dir = TempDir();
        int folderId;
        using (var db = _db.NewContext())
        {
            var folder = new RootFolder { Path = dir };
            db.RootFolders.Add(folder);
            db.SaveChanges();
            folderId = folder.Id;
            db.Series.Add(new Series { Title = "X", RootFolderId = folderId });
            db.SaveChanges();
        }

        Assert.IsType<ConflictObjectResult>(await Controller().Delete(folderId, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_names_archived_series_only_when_no_active_series_remain()
    {
        var folderId = SeedFolder();
        using (var db = _db.NewContext())
        {
            var archivedAt = DateTime.UtcNow;
            db.Series.Add(new Series { Title = "A", RootFolderId = folderId, ArchivedAt = archivedAt });
            db.Series.Add(new Series { Title = "B", RootFolderId = folderId, ArchivedAt = archivedAt });
            db.SaveChanges();
        }

        Assert.Equal("error.rootFolder.inUseArchived(count=2)", ConflictError(
            await Controller().Delete(folderId, CancellationToken.None)));

        using (var db = _db.NewContext())
        {
            db.Series.Add(new Series { Title = "C", RootFolderId = folderId });
            db.SaveChanges();
        }

        Assert.Equal("error.rootFolder.inUse", ConflictError(
            await Controller().Delete(folderId, CancellationToken.None)));
    }

    /// <summary>
    /// Nothing visits an inventory row whose root is gone, so the delete retires them itself. Hard
    /// deleting is out: versions, operations and history point at the rows.
    /// </summary>
    [Fact]
    public async Task Delete_retires_the_folders_health_rows_and_resolves_their_findings()
    {
        var folderId = SeedFolder();
        var keptId = SeedFolder();
        int goneId, keptRowId;
        using (var db = _db.NewContext())
        {
            var gone = new HealthFile { RootFolderId = folderId, RelativePath = "a.cbz", Status = "complete", ContentHash = "H" };
            var kept = new HealthFile { RootFolderId = keptId, RelativePath = "b.cbz", Status = "complete", ContentHash = "K" };
            db.HealthFiles.AddRange(gone, kept);
            db.SaveChanges();
            goneId = gone.Id;
            keptRowId = kept.Id;
            db.HealthFindings.Add(new HealthFinding { FileId = gone.Id, Version = gone.Version, Kind = "unlinked", MessageKey = "health.finding.unlinked" });
            db.HealthFindings.Add(new HealthFinding { FileId = kept.Id, Version = kept.Version, Kind = "unlinked", MessageKey = "health.finding.unlinked" });
            db.SaveChanges();
        }

        Assert.IsType<NoContentResult>(await Controller().Delete(folderId, CancellationToken.None));

        using var check = _db.NewContext();
        Assert.True(check.HealthFiles.Single(f => f.Id == goneId).Removed);
        Assert.Equal("resolved", check.HealthFindings.Single(f => f.FileId == goneId).State);
        Assert.False(check.HealthFiles.Single(f => f.Id == keptRowId).Removed);
        Assert.Equal("open", check.HealthFindings.Single(f => f.FileId == keptRowId).State);
        Assert.Empty(check.RootFolders.Where(f => f.Id == folderId));
    }

    private static string ConflictError(IActionResult result) =>
        (string)((ConflictObjectResult)result).Value!.GetType().GetProperty("error")!.GetValue(((ConflictObjectResult)result).Value)!;

    [Fact]
    public async Task Delete_rejects_a_folder_whose_series_are_outside_the_callers_scope()
    {
        var folderId = SeedFolder();
        int adminId;
        using (var db = _db.NewContext())
        {
            db.Series.Add(new Series { Title = "X", RootFolderId = folderId });
            db.SaveChanges();
            adminId = _db.SeedUser("restricted-admin", MakiPermission.Admin);
        }

        var controller = new RootFolderController(
            new TestLocalizer(), _db.NewContext(adminId, allRootFolders: false), _snapshots,
            new TestCurrentUser(adminId, permissions: MakiPermission.Admin, allRootFolders: false));

        Assert.IsType<ConflictObjectResult>(await controller.Delete(folderId, CancellationToken.None));
    }

    [Fact]
    public async Task Delete_removes_an_unused_folder()
    {
        var dir = TempDir();
        int folderId;
        using (var db = _db.NewContext())
        {
            var folder = new RootFolder { Path = dir };
            db.RootFolders.Add(folder);
            db.SaveChanges();
            folderId = folder.Id;
        }

        var result = await Controller().Delete(folderId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        using var check = _db.NewContext();
        Assert.Empty(check.RootFolders);
    }

    /// <summary>
    /// SQLite reuses the highest rowid, so a snapshot still naming a deleted folder would grant its
    /// holder whatever folder is added next under that id.
    /// </summary>
    [Fact]
    public async Task Delete_evicts_the_snapshots_of_users_granted_the_folder()
    {
        var granted = _db.SeedUser("granted", MakiPermission.None, allRootFolders: false);
        var other = _db.SeedUser("other", MakiPermission.None, allRootFolders: false);
        int folderId;
        using (var db = _db.NewContext())
        {
            var folder = new RootFolder { Path = TempDir() };
            db.RootFolders.Add(folder);
            db.SaveChanges();
            folderId = folder.Id;
            db.UserRootFolders.Add(new UserRootFolder { UserId = granted, RootFolderId = folderId });
            db.SaveChanges();
        }

        foreach (var userId in new[] { granted, other })
        {
            _snapshots.Set(new UserSnapshot(userId, "u", MakiPermission.None, false,
                new HashSet<int> { folderId }, "safe"), _snapshots.Generation);
        }

        Assert.IsType<NoContentResult>(await Controller().Delete(folderId, CancellationToken.None));

        Assert.Null(_snapshots.Get(granted));
        Assert.NotNull(_snapshots.Get(other));
    }
}
