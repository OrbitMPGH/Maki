using Maki.Api.Auth;
using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// The owner of an account is told when something that guards it changes: password, second factor,
/// keys and tokens, and what an administrator grants. The alerts carry what changed and when, never
/// a secret, and cannot be switched off in the notification preferences.
/// </summary>
public sealed class AccountSecurityAlertTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private readonly TestDb _db = new();
    private readonly StoppedClock _clock = new(new DateTimeOffset(2026, 10, 10, 14, 5, 0, TimeSpan.Zero));
    private readonly RecordingInbox _inbox = new();

    public void Dispose() => _db.Dispose();

    private int SeedWithPassword(string name, MakiPermission permissions = MakiPermission.None, bool allRootFolders = true) =>
        _db.SeedUser(name, permissions, allRootFolders, configure: u => u.PasswordHash = IdentityTestKit.Hash(u, Password));

    private AccountSecurityAlerts Alerts() => new(_inbox, _clock);

    private AccountController Account(MakiDbContext db, int userId)
    {
        var users = IdentityTestKit.UserManager(db);
        return new AccountController(
            new TestLocalizer(), db, users, new TestSignInManager(users), new TestCurrentUser(userId, "ada"),
            new AuthEventLogger(db, _clock), new OidcRuntimeOptions(), _clock,
            new UserSnapshotCache(new MemoryCache(new MemoryCacheOptions())), alerts: Alerts())
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
            new UserSnapshotCache(new MemoryCache(new MemoryCacheOptions())), new TestSignInManager(users), Alerts())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private SettingsController Settings(int userId, MakiDbContext db) => new(
        localizer: new TestLocalizer(), userLocales: new TestUserLocaleResolver(),
        settings: null!, naming: null!, flareSolverr: null!, prowlarr: null!, qbittorrent: null!,
        kavita: null!, sourceRegistry: null!, sourceAvailability: null!,
        mangaBakaDump: null!, embeddingModel: null!, embeddingStore: null!, embeddingStatus: null!,
        embeddingIndexer: null!, prebuiltIndex: null!, recoGraph: null!,
        recoGraphCache: null!, coReadInstaller: null!, coReadCache: null!, readerCohortInstaller: null!,
        readerCohortCache: null!, tasteVectorInstaller: null!, vectorIndexCache: null!,
        modelSwitcher: null!, db: db, updateCheck: null!, currentUser: new TestCurrentUser(userId),
        userSettings: new UserSettingsService(db, new TestCurrentUser(userId)),
        kavitaUser: null!, kavitaLive: null!, schedulerFactory: null!, scopeFactory: null!,
        logger: NullLogger<SettingsController>.Instance, alerts: Alerts());

    private (string Key, IReadOnlyDictionary<string, object?> Params, int UserId) Alert()
    {
        var (type, message, audience) = Assert.Single(_inbox.Raised);
        Assert.Equal(InboxEventType.AccountSecurity, type);
        Assert.Equal(InboxAudienceKind.User, audience.Kind);
        return (message.Key, message.Params!, audience.UserId);
    }

    private static void AssertNoSecrets(IReadOnlyDictionary<string, object?> args)
    {
        Assert.Equal("2026-10-10 14:05 UTC", args["when"]);
        Assert.DoesNotContain(args.Values, v => v is string s && (s.Contains(Password) || s.StartsWith("mk_")));
    }

    [Fact]
    public async Task Changing_your_password_alerts_you()
    {
        var userId = SeedWithPassword("ada");
        using var db = _db.NewContext(userId);

        await Account(db, userId).ChangePassword(new ChangePasswordRequest(Password, "New-Password-123"), default);

        var (key, args, recipient) = Alert();
        Assert.Equal(AccountSecurityAlerts.PasswordChanged, key);
        Assert.Equal(userId, recipient);
        AssertNoSecrets(args);
    }

    [Fact]
    public async Task A_rejected_password_change_alerts_nobody()
    {
        var userId = SeedWithPassword("ada");
        using var db = _db.NewContext(userId);

        await Account(db, userId).ChangePassword(new ChangePasswordRequest("not the password", "New-Password-123"), default);

        Assert.Empty(_inbox.Raised);
    }

    [Fact]
    public async Task Turning_two_factor_on_and_off_alerts_you()
    {
        var userId = SeedWithPassword("ada");
        using var db = _db.NewContext(userId);

        var enable = await Account(db, userId).EnableTwoFactor(
            new EnableTwoFactorRequest(IdentityTestKit.ValidAuthenticatorCode, Password), default);
        Assert.IsType<OkObjectResult>(enable);
        Assert.Equal(AccountSecurityAlerts.TwoFactorEnabled, Alert().Key);

        _inbox.Raised.Clear();
        var disable = await Account(db, userId).DisableTwoFactor(new DisableTwoFactorRequest(Password), default);
        Assert.IsType<NoContentResult>(disable);
        Assert.Equal(AccountSecurityAlerts.TwoFactorDisabled, Alert().Key);
    }

    [Fact]
    public async Task Creating_an_api_key_alerts_you_with_its_name_but_never_the_key()
    {
        var userId = SeedWithPassword("ada");
        using var db = _db.NewContext(userId);

        var result = await Account(db, userId).CreateApiKey(
            new CreateApiKeyRequest("backup script", UserApiKeyScope.Full, Password), default);

        var created = Assert.IsType<CreatedApiKeyDto>(Assert.IsType<OkObjectResult>(result).Value);
        var (key, args, _) = Alert();
        Assert.Equal(AccountSecurityAlerts.ApiKeyCreated, key);
        Assert.Equal("backup script", args["name"]);
        AssertNoSecrets(args);
        Assert.DoesNotContain(args.Values, v => v is string s && s.Contains(created.Secret));
    }

    [Fact]
    public async Task Minting_then_rotating_an_opds_token_alerts_you_each_time()
    {
        var userId = SeedWithPassword("reader", MakiPermission.UseOpds);
        using var db = _db.NewContext(userId);
        var users = IdentityTestKit.UserManager(db);

        await Settings(userId, db).SetOpds(
            new SettingsController.OpdsSettings(true, true, Password), users, new TestSignInManager(users), default);
        Assert.Equal(AccountSecurityAlerts.OpdsTokenCreated, Alert().Key);

        _inbox.Raised.Clear();
        await Settings(userId, db).RotateOpdsToken(new ConfirmPasswordRequest(Password), users, new TestSignInManager(users), default);
        var (key, args, _) = Alert();
        Assert.Equal(AccountSecurityAlerts.OpdsTokenRotated, key);
        AssertNoSecrets(args);
    }

    [Fact]
    public async Task An_admin_password_reset_alerts_the_user_but_not_the_admin_resetting_their_own()
    {
        var adminId = SeedWithPassword("admin", MakiPermission.Admin);
        var readerId = SeedWithPassword("reader");
        using var db = _db.NewContext();

        await Users(db, adminId).Update(readerId, new SaveUserRequest(
            null, "Another-Password-456", null, null, null, null, null, null), default);
        var (key, _, recipient) = Alert();
        Assert.Equal(AccountSecurityAlerts.PasswordReset, key);
        Assert.Equal(readerId, recipient);

        _inbox.Raised.Clear();
        await Users(db, adminId).Update(adminId, new SaveUserRequest(
            null, "Another-Password-456", null, null, null, null, null, null), default);
        Assert.Empty(_inbox.Raised);
    }

    [Fact]
    public async Task An_admin_resetting_two_factor_alerts_the_user()
    {
        var adminId = SeedWithPassword("admin", MakiPermission.Admin);
        var readerId = SeedWithPassword("reader");
        using var db = _db.NewContext();

        await Users(db, adminId).ResetTwoFactor(readerId, default);

        var (key, _, recipient) = Alert();
        Assert.Equal(AccountSecurityAlerts.TwoFactorReset, key);
        Assert.Equal(readerId, recipient);
    }

    [Fact]
    public async Task Changing_permissions_or_folder_grants_alerts_the_user_only_when_they_actually_change()
    {
        var adminId = SeedWithPassword("admin", MakiPermission.Admin);
        var readerId = SeedWithPassword("reader", MakiPermission.AddSeries, allRootFolders: false);
        int folderId;
        using (var seed = _db.NewContext())
        {
            var folder = new RootFolder { Path = "/library" };
            seed.RootFolders.Add(folder);
            seed.SaveChanges();
            folderId = folder.Id;
        }

        using var db = _db.NewContext();

        await Users(db, adminId).Update(readerId, new SaveUserRequest(
            null, null, "Reader", MakiPermission.AddSeries, null, null, null, null), default);
        Assert.Empty(_inbox.Raised);

        await Users(db, adminId).Update(readerId, new SaveUserRequest(
            null, null, null, MakiPermission.AddSeries | MakiPermission.UseOpds, null, null, null, null), default);
        Assert.Equal(AccountSecurityAlerts.PermissionsChanged, Alert().Key);

        _inbox.Raised.Clear();
        await Users(db, adminId).Update(readerId, new SaveUserRequest(
            null, null, null, null, null, null, [folderId], null), default);
        Assert.Equal(AccountSecurityAlerts.FolderAccessChanged, Alert().Key);

        _inbox.Raised.Clear();
        await Users(db, adminId).Update(readerId, new SaveUserRequest(
            null, null, null, null, null, null, [folderId], null), default);
        Assert.Empty(_inbox.Raised);
    }

    [Fact]
    public void Account_security_alerts_cannot_be_switched_off_by_a_stored_preference()
    {
        var off = InboxPrefsSpec.Parse("""{"types":{"accountSecurity":false,"downloadFailed":false}}""");

        Assert.True(off.Wants(InboxEventType.AccountSecurity));
        Assert.False(off.Wants(InboxEventType.DownloadFailed));
        Assert.True(off.Merge().Types!["accountSecurity"]);
        Assert.False(InboxEventTypes.DefaultsOff(InboxEventType.AccountSecurity));
        Assert.False(InboxEventTypes.IsAdminOnly(InboxEventType.AccountSecurity));
    }
}
