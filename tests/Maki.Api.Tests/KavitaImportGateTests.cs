using Maki.Api.Controllers;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The Kavita read import writes the bound user's progress, so only that user or an admin may start
/// it or read its status.
/// </summary>
public sealed class KavitaImportGateTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private ReaderController Controller(int userId, MakiPermission permissions)
    {
        var scopeFactory = _db.ScopeFactory();
        var settings = new SettingsService(scopeFactory);
        var resolver = new KavitaUserResolver(scopeFactory, settings);
        var import = new KavitaReadImportService(
            scopeFactory, settings, null!, null!, null!, resolver, NullLogger<KavitaReadImportService>.Instance);
        return new ReaderController(
            new TestLocalizer(), _db.NewContext(userId), null!, null!, null!, import, null!, null!, null!,
            NullLogger<ReaderController>.Instance, new TestCurrentUser(userId, permissions: permissions), resolver);
    }

    private static int StatusOf(IActionResult result) => result switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => throw new InvalidOperationException(result.GetType().Name)
    };

    [Fact]
    public async Task A_reader_who_is_not_the_bound_user_is_refused()
    {
        var admin = _db.SeedUser("admin");
        var reader = _db.SeedUser("reader", MakiPermission.None);
        _db.SetConfig((SettingKeys.KavitaUserId, admin.ToString()));

        Assert.Equal(403, StatusOf(await Controller(reader, MakiPermission.None).StartKavitaImport(CancellationToken.None)));
        Assert.Equal(403, StatusOf(await Controller(reader, MakiPermission.None).KavitaImportStatus(CancellationToken.None)));
    }

    [Fact]
    public async Task The_bound_user_may_read_the_status_without_being_admin()
    {
        _db.SeedUser("admin");
        var reader = _db.SeedUser("reader", MakiPermission.None);
        _db.SetConfig((SettingKeys.KavitaUserId, reader.ToString()));

        Assert.Equal(200, StatusOf(await Controller(reader, MakiPermission.None).KavitaImportStatus(CancellationToken.None)));
    }

    [Fact]
    public async Task An_admin_may_read_the_status_while_another_user_is_bound()
    {
        var admin = _db.SeedUser("admin");
        var reader = _db.SeedUser("reader", MakiPermission.None);
        _db.SetConfig((SettingKeys.KavitaUserId, reader.ToString()));

        Assert.Equal(200, StatusOf(await Controller(admin, MakiPermission.Admin).KavitaImportStatus(CancellationToken.None)));
    }

    /// <summary>With nobody configured the lowest enabled admin is bound, never an arbitrary reader.</summary>
    [Fact]
    public async Task With_no_binding_a_reader_is_refused()
    {
        _db.SeedUser("admin");
        var reader = _db.SeedUser("reader", MakiPermission.None);

        Assert.Equal(403, StatusOf(await Controller(reader, MakiPermission.None).StartKavitaImport(CancellationToken.None)));
    }
}
