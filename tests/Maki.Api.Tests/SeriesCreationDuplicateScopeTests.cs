using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Metadata;
using Maki.Core.Security;
using Maki.Data.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The library is shared, so "this work is already here" has to be true whichever root folder the
/// existing copy sits in and whichever folders the caller may see.
/// </summary>
public sealed class SeriesCreationDuplicateScopeTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

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

    [Fact]
    public async Task A_caller_without_the_existing_copys_folder_cannot_add_a_second_copy()
    {
        var user = _db.SeedUser("restricted", MakiPermission.AddSeries, allRootFolders: false);
        int grantedRoot;
        using (var db = _db.NewContext())
        {
            var granted = new RootFolder { Path = "/granted" };
            db.RootFolders.Add(granted);
            db.SaveChanges();
            grantedRoot = granted.Id;
            db.UserRootFolders.Add(new UserRootFolder { UserId = user, RootFolderId = granted.Id });
            db.SaveChanges();
        }

        var existing = _db.SeedSeries("Berserk", configure: s => s.MangaBakaId = 42);

        using var scoped = _db.NewContext(user, allRootFolders: false);
        Assert.False(await scoped.Series.AnyAsync(s => s.Id == existing));
        var creation = new SeriesCreationService(
            scoped, [new Provider()],
            coverService: null!, sourceMatchService: null!, chapterSyncService: null!,
            sourceMatchQueue: new SourceMatchQueue(),
            stats: null!, identity: null!, appSettings: new FakeAppSettings(),
            naming: new NamingService(new FakeAppSettings()),
            notifications: new RecordingNotifications(), locales: new TestUserLocaleResolver(),
            catalog: new TestLocalizer(), logger: NullLogger<SeriesCreationService>.Instance);

        var result = await creation.CreateAsync("42", grantedRoot, monitored: true, null, default, attributedUserId: user);

        Assert.Equal(SeriesCreationError.AlreadyInLibrary, result.Error);
        using var verify = _db.NewContext();
        Assert.Equal(1, await verify.Series.CountAsync(s => s.MangaBakaId == 42));
    }
}
