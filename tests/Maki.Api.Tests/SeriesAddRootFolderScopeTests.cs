using System.Text.Json;
using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Core.Entities;
using Maki.Core.Security;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Tests;

/// <summary>
/// <c>POST /series</c> only accepts a root folder the caller may see. The folder list is scoped the
/// same way, so this is what stops a non-admin from adding into a folder by guessing its id.
/// </summary>
public sealed class SeriesAddRootFolderScopeTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private SeriesController Series(ICurrentUser user) =>
        new(new TestLocalizer(), _db.NewContext(), null!, null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, currentUser: user, null!, null!,
            null!, null!);

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
}
