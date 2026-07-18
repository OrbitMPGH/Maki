using Maki.Api.Controllers;
using Maki.Core.Entities;
using Microsoft.AspNetCore.Mvc;

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

    private RootFolderController Controller() => new(_db.NewContext());

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
}
