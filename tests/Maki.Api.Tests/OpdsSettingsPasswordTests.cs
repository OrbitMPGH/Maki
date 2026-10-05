using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maki.Api.Tests;

/// <summary>
/// Minting an OPDS token asks for the account password, the same as minting an API key: the token
/// outlives the session that created it, so a hijacked session alone must not be enough.
/// </summary>
public sealed class OpdsSettingsPasswordTests : IDisposable
{
    private const string Password = "correct horse battery staple";
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private SettingsController Controller(int userId, MakiDbContext db) => new(
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
        logger: NullLogger<SettingsController>.Instance);

    private int SeedReader() => _db.SeedUser("reader", MakiPermission.UseOpds,
        configure: u => u.PasswordHash = IdentityTestKit.Hash(u, Password));

    private int LiveOpdsKeys(int userId)
    {
        using var db = _db.NewContext();
        return db.UserApiKeys.Count(k => k.UserId == userId && k.Scope == UserApiKeyScope.Opds && k.RevokedAt == null);
    }

    private static string CodeOf(IActionResult result)
    {
        var body = Assert.IsType<BadRequestObjectResult>(result).Value!;
        return (string)body.GetType().GetProperty("code")!.GetValue(body)!;
    }

    [Fact]
    public async Task Enabling_without_the_password_mints_nothing_and_saves_nothing()
    {
        var userId = SeedReader();
        using var db = _db.NewContext(userId);
        var users = IdentityTestKit.UserManager(db);

        var result = await Controller(userId, db).SetOpds(
            new SettingsController.OpdsSettings(true, true), users, new TestSignInManager(users), default);

        Assert.Equal("error.account.incorrectPassword", CodeOf(result));
        Assert.Equal(0, LiveOpdsKeys(userId));
        using var check = _db.NewContext();
        Assert.DoesNotContain(check.UserSettings, s => s.UserId == userId && s.Key == SettingKeys.OpdsEnabled);
    }

    [Fact]
    public async Task Enabling_with_the_password_mints_the_first_token()
    {
        var userId = SeedReader();
        using var db = _db.NewContext(userId);
        var users = IdentityTestKit.UserManager(db);

        var result = await Controller(userId, db).SetOpds(
            new SettingsController.OpdsSettings(true, true, Password), users, new TestSignInManager(users), default);

        var body = Assert.IsType<SettingsController.OpdsSettingsResponse>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.NotNull(body.FeedUrl);
        Assert.Equal(1, LiveOpdsKeys(userId));
    }

    [Fact]
    public async Task Toggling_progress_on_an_existing_token_needs_no_password()
    {
        var userId = SeedReader();
        _db.SeedApiKey(userId, UserApiKeyScope.Opds);
        using var db = _db.NewContext(userId);
        var users = IdentityTestKit.UserManager(db);

        var result = await Controller(userId, db).SetOpds(
            new SettingsController.OpdsSettings(true, false), users, new TestSignInManager(users), default);

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Rotating_needs_the_password()
    {
        var userId = SeedReader();
        _db.SeedApiKey(userId, UserApiKeyScope.Opds);
        using var db = _db.NewContext(userId);
        var users = IdentityTestKit.UserManager(db);
        var controller = Controller(userId, db);

        var refused = await controller.RotateOpdsToken(
            new ConfirmPasswordRequest("wrong password"), users, new TestSignInManager(users), default);
        Assert.Equal("error.account.incorrectPassword", CodeOf(refused));

        var rotated = await controller.RotateOpdsToken(
            new ConfirmPasswordRequest(Password), users, new TestSignInManager(users), default);
        Assert.IsType<OkObjectResult>(rotated);
        Assert.Equal(1, LiveOpdsKeys(userId));
    }
}
