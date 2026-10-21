using Maki.Api.Auth;
using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// A push subscription belongs to a browser session, so every path that signs other sessions out
/// also forgets the user's devices. A revoked phone must not keep receiving pushes.
/// </summary>
public sealed class PushRevocationTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private readonly TestDb _db = new();
    private readonly StoppedClock _clock = new(new DateTimeOffset(2026, 10, 10, 14, 5, 0, TimeSpan.Zero));

    public void Dispose() => _db.Dispose();

    private int Seed(string name, MakiPermission permissions = MakiPermission.None) =>
        _db.SeedUser(name, permissions, configure: u => u.PasswordHash = IdentityTestKit.Hash(u, Password));

    private void Subscribe(int userId, string device)
    {
        using var db = _db.NewContext();
        db.PushSubscriptions.Add(new PushSubscription
        {
            UserId = userId, Endpoint = $"https://push.example.com/{device}", P256dh = "k", Auth = "a",
            CreatedAt = _clock.GetUtcNow().UtcDateTime,
        });
        db.SaveChanges();
    }

    private int Devices(int userId)
    {
        using var db = _db.NewContext();
        return db.PushSubscriptions.Count(s => s.UserId == userId);
    }

    private AccountController Account(MakiDbContext db, int userId)
    {
        var users = IdentityTestKit.UserManager(db);
        return new AccountController(
            new TestLocalizer(), db, users, new TestSignInManager(users), new TestCurrentUser(userId, "ada"),
            new AuthEventLogger(db, _clock), new OidcRuntimeOptions(), _clock,
            new UserSnapshotCache(new MemoryCache(new MemoryCacheOptions())))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private UsersController Users(MakiDbContext db, int adminId)
    {
        var users = IdentityTestKit.UserManager(db);
        return new(new TestLocalizer(), db, users, new AdminGuard(db),
            new TestCurrentUser(adminId, "admin"), new AuthEventLogger(db, _clock), _clock,
            NullLogger<UsersController>.Instance, new OidcRuntimeOptions(), new NoopHubContext(),
            new UserSnapshotCache(new MemoryCache(new MemoryCacheOptions())), new TestSignInManager(users))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [Fact]
    public async Task Signing_out_everywhere_forgets_every_device_of_that_user_only()
    {
        var ada = Seed("ada");
        var bob = Seed("bob");
        Subscribe(ada, "phone");
        Subscribe(ada, "laptop");
        Subscribe(bob, "tablet");
        using var db = _db.NewContext(ada);

        Assert.IsType<NoContentResult>(await Account(db, ada).RevokeSessions(default));

        Assert.Equal(0, Devices(ada));
        Assert.Equal(1, Devices(bob));
    }

    [Fact]
    public async Task Changing_your_password_forgets_your_devices()
    {
        var ada = Seed("ada");
        Subscribe(ada, "phone");
        using var db = _db.NewContext(ada);

        Assert.IsType<NoContentResult>(await Account(db, ada).ChangePassword(
            new ChangePasswordRequest(Password, "New-Password-123"), default));

        Assert.Equal(0, Devices(ada));
    }

    [Fact]
    public async Task Turning_two_factor_off_forgets_your_devices()
    {
        var ada = Seed("ada");
        using var db = _db.NewContext(ada);
        Assert.IsType<OkObjectResult>(await Account(db, ada).EnableTwoFactor(
            new EnableTwoFactorRequest(IdentityTestKit.ValidAuthenticatorCode, Password), default));
        Subscribe(ada, "phone");

        Assert.IsType<NoContentResult>(await Account(db, ada).DisableTwoFactor(new DisableTwoFactorRequest(Password), default));

        Assert.Equal(0, Devices(ada));
    }

    [Fact]
    public async Task An_admin_password_reset_or_disable_forgets_the_users_devices()
    {
        var admin = Seed("admin", MakiPermission.Admin);
        var reader = Seed("reader");
        var other = Seed("other");
        Subscribe(reader, "phone");
        Subscribe(other, "tablet");
        Subscribe(admin, "desk");
        using var db = _db.NewContext();

        await Users(db, admin).Update(reader, new SaveUserRequest(
            null, "Another-Password-456", null, null, null, null, null, null), default);
        Assert.Equal(0, Devices(reader));

        await Users(db, admin).Update(other, new SaveUserRequest(
            null, null, null, null, null, null, null, true), default);
        Assert.Equal(0, Devices(other));
        Assert.Equal(1, Devices(admin));
    }

    [Fact]
    public async Task An_admin_edit_that_takes_nothing_away_keeps_the_devices()
    {
        var admin = Seed("admin", MakiPermission.Admin);
        var reader = Seed("reader");
        Subscribe(reader, "phone");
        using var db = _db.NewContext();

        await Users(db, admin).Update(reader, new SaveUserRequest(
            null, null, "Reader", null, null, null, null, null), default);

        Assert.Equal(1, Devices(reader));
    }

    [Fact]
    public async Task An_admin_two_factor_reset_forgets_the_users_devices()
    {
        var admin = Seed("admin", MakiPermission.Admin);
        var reader = Seed("reader");
        Subscribe(reader, "phone");
        using var db = _db.NewContext();

        await Users(db, admin).ResetTwoFactor(reader, default);

        Assert.Equal(0, Devices(reader));
    }
}
