using Maki.Api.Hubs;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Metadata;
using Maki.Core.Security;
using Maki.Data.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// An import must not hand a restricted user a second copy of a work, nor re-point a series they
/// cannot see at their own root folder.
/// </summary>
public sealed class LibraryImportDuplicateScopeTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "maki-import-scope-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class Provider : IMetadataProvider
    {
        public string Name => "stub";

        public Task<IReadOnlyList<MetadataSearchResult>> SearchAsync(
            string query, string maxContentRating, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MetadataSearchResult>>([]);

        public Task<SeriesMetadata?> GetAsync(string providerId, CancellationToken ct = default) =>
            Task.FromResult<SeriesMetadata?>(new SeriesMetadata
            {
                ProviderId = providerId, Title = "Berserk", MangaBakaId = int.Parse(providerId),
            });
    }

    private sealed class SilentBroadcaster() : EventBroadcaster(null!, null!)
    {
        public override Task ImportProgress(
            string folderName, string stage, int? current = null, int? total = null,
            bool done = false, bool success = false, string? error = null, string? operationId = null) =>
            Task.CompletedTask;
    }

    [Fact]
    public async Task A_caller_without_the_existing_copys_folder_cannot_import_a_second_copy()
    {
        var user = _db.SeedUser("restricted", MakiPermission.ImportLibrary, allRootFolders: false);
        int grantedRoot;
        Directory.CreateDirectory(Path.Combine(_root, "Berserk"));
        using (var db = _db.NewContext())
        {
            var granted = new RootFolder { Path = _root };
            db.RootFolders.Add(granted);
            db.SaveChanges();
            grantedRoot = granted.Id;
            db.UserRootFolders.Add(new UserRootFolder { UserId = user, RootFolderId = granted.Id });
            db.SaveChanges();
        }

        var existing = _db.SeedSeries("Berserk", configure: s => s.MangaBakaId = 42);
        int existingRoot;
        using (var db = _db.NewContext())
        {
            existingRoot = db.Series.Single(s => s.Id == existing).RootFolderId;
        }

        using var scoped = _db.NewContext(user, allRootFolders: false);
        var service = new LibraryImportService(
            scoped, [new Provider()], null!, null!, new SourceMatchQueue(), new SilentBroadcaster(), new FakeAppSettings(),
            new NamingService(new FakeAppSettings()), null!, null!, new TestLocalizer(),
            new TestCurrentUser(user, permissions: MakiPermission.ImportLibrary, allRootFolders: false,
                rootFolderIds: new HashSet<int> { grantedRoot }),
            NullLogger<LibraryImportService>.Instance);

        var result = await service.ImportAsync(grantedRoot, new ImportRequestItem("Berserk", "42"));

        Assert.False(result.Success);
        Assert.Contains("alreadyInLibrary", result.Error);
        using var verify = _db.NewContext();
        var rows = await verify.Series.Where(s => s.MangaBakaId == 42).ToListAsync();
        Assert.Equal(existingRoot, Assert.Single(rows).RootFolderId);
    }
}
