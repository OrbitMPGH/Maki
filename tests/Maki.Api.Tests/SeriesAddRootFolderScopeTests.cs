using System.Text.Json;
using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Metadata;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// <c>POST /series</c> only accepts a root folder the caller may see. The folder list is scoped the
/// same way, so this is what stops a non-admin from adding into a folder by guessing its id.
/// </summary>
public sealed class SeriesAddRootFolderScopeTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private SeriesController Series(ICurrentUser user, SeriesCreationService? creation = null, MakiDbContext? db = null) =>
        new(new TestLocalizer(), db ?? _db.NewContext(), null!, null!, null!, null!, creation!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, currentUser: user, null!,
            null!, null!, null!);

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

    private int SeedFolder()
    {
        using var db = _db.NewContext();
        var folder = new RootFolder
        {
            Path = Path.Combine(Path.GetTempPath(), "maki-scope-" + Guid.NewGuid().ToString("N")),
        };
        db.RootFolders.Add(folder);
        db.SaveChanges();
        return folder.Id;
    }

    private static string Code(IActionResult result) =>
        JsonSerializer.SerializeToElement(((ObjectResult)result).Value).GetProperty("code").GetString()!;

    [Fact]
    public async Task Add_rejects_a_folder_the_caller_was_not_granted_as_not_found()
    {
        var granted = SeedFolder();
        var other = SeedFolder();
        var user = new TestCurrentUser(
            2, permissions: MakiPermission.AddSeries, allRootFolders: false, rootFolderIds: new HashSet<int> { granted });

        var result = await Series(user).Add(
            new AddSeriesRequest("123", other, ClientMutationId: Guid.NewGuid()), default);

        Assert.Equal("error.series.rootFolderNotFound", Code(result));
    }

    [Fact]
    public async Task Add_rejects_every_folder_for_a_caller_with_no_grants()
    {
        var folder = SeedFolder();
        var user = new TestCurrentUser(2, permissions: MakiPermission.AddSeries, allRootFolders: false);

        var result = await Series(user).Add(
            new AddSeriesRequest("123", folder, ClientMutationId: Guid.NewGuid()), default);

        Assert.Equal("error.series.rootFolderNotFound", Code(result));
    }

    [Fact]
    public async Task Add_of_an_archived_series_answers_archivedExists_with_the_series_id()
    {
        var archived = _db.SeedSeries("Berserk", configure: s =>
        {
            s.MangaBakaId = 42;
            s.ArchivedAt = DateTime.UtcNow;
        });
        int rootFolder;
        using (var db = _db.NewContext())
            rootFolder = (await db.Series.SingleAsync(s => s.Id == archived)).RootFolderId;
        var user = new TestCurrentUser(2, permissions: MakiPermission.AddSeries);
        var scoped = _db.NewContext(user.UserId);
        var creation = new SeriesCreationService(
            scoped, [new Provider()],
            coverService: null!, sourceMatchService: null!, chapterSyncService: null!,
            sourceMatchQueue: new SourceMatchQueue(),
            stats: null!, identity: null!, appSettings: new FakeAppSettings(),
            naming: new NamingService(new FakeAppSettings()),
            notifications: new RecordingNotifications(), locales: new TestUserLocaleResolver(),
            catalog: new TestLocalizer(), localizer: new TestLocalizer(), logger: NullLogger<SeriesCreationService>.Instance);

        var result = await Series(user, creation, scoped).Add(
            new AddSeriesRequest("42", rootFolder, ClientMutationId: Guid.NewGuid()), default);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var body = JsonSerializer.SerializeToElement(conflict.Value);
        Assert.Equal("error.series.archivedExists", body.GetProperty("code").GetString());
        Assert.Equal(archived, body.GetProperty("seriesId").GetInt32());
    }

    [Fact]
    public async Task Add_of_an_archived_series_the_caller_cannot_see_is_the_plain_already_exists()
    {
        _db.SeedSeries("Berserk", configure: s =>
        {
            s.MangaBakaId = 42;
            s.ArchivedAt = DateTime.UtcNow;
        });
        var granted = SeedFolder();
        var userId = _db.SeedUser("scoped", MakiPermission.AddSeries, allRootFolders: false);
        using (var db = _db.NewContext())
        {
            db.UserRootFolders.Add(new Maki.Data.Identity.UserRootFolder { UserId = userId, RootFolderId = granted });
            db.SaveChanges();
        }

        var user = new TestCurrentUser(
            userId, permissions: MakiPermission.AddSeries, allRootFolders: false, rootFolderIds: new HashSet<int> { granted });
        var scoped = _db.NewContext(userId, allRootFolders: false);
        var creation = new SeriesCreationService(
            scoped, [new Provider()],
            coverService: null!, sourceMatchService: null!, chapterSyncService: null!,
            sourceMatchQueue: new SourceMatchQueue(),
            stats: null!, identity: null!, appSettings: new FakeAppSettings(),
            naming: new NamingService(new FakeAppSettings()),
            notifications: new RecordingNotifications(), locales: new TestUserLocaleResolver(),
            catalog: new TestLocalizer(), localizer: new TestLocalizer(), logger: NullLogger<SeriesCreationService>.Instance);

        var result = await Series(user, creation, scoped).Add(
            new AddSeriesRequest("42", granted, ClientMutationId: Guid.NewGuid()), default);

        Assert.Equal("error.series.alreadyExists", Code(result));
        Assert.False(JsonSerializer.SerializeToElement(((ObjectResult)result).Value).TryGetProperty("seriesId", out _));
    }
}
