using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Opds;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;

namespace Maki.Api.Tests;

/// <summary>
/// The OPDS catalogue's authentication boundary and its one deviation from the built-in reader's
/// progress rule. Both used to live inline in the controller, where nothing could reach them.
/// </summary>
public sealed class OpdsAccessTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private Task<OpdsAccess?> ResolveAsync(string? token) =>
        new OpdsAccessService(_db.NewContext(), TimeProvider.System)
            .ResolveAsync(token, CancellationToken.None);

    /// <summary>
    /// Turns the catalogue on for <em>one user</em>. The two switches are per-user rows rather than
    /// AppConfig keys, so one reader can disable their own feed, or turn progress tracking off for a
    /// prefetching app, without touching anybody else's.
    /// </summary>
    private void EnableCatalogue(int userId, bool trackProgress = true) =>
        _db.SetUserConfig(
            userId,
            (SettingKeys.OpdsEnabled, "true"),
            (SettingKeys.OpdsTrackProgress, trackProgress ? "true" : "false"));

    [Fact]
    public async Task ResolvesOnAContextScopedToNobody()
    {
        // What the app actually does. An OPDS request carries no cookie and no API-key header, so
        // CurrentUserMiddleware narrows the scope to nobody before this runs — resolving the token is
        // what decides who the caller is. A settings read that respected the user-owned query filter
        // would find no rows here and turn every valid feed URL into a 404.
        var userId = _db.SeedUser();
        var token = _db.SeedApiKey(userId, UserApiKeyScope.Opds);
        EnableCatalogue(userId);

        var nobody = new DataScope();
        nobody.SetNobody();
        using var db = new MakiDbContext(_db.Options, nobody);

        var access = await new OpdsAccessService(db, TimeProvider.System)
            .ResolveAsync(token, CancellationToken.None);

        Assert.NotNull(access);
        Assert.Equal(userId, access.UserId);
        Assert.True(access.TrackProgress);
    }

    // ---- the token check ----

    [Fact]
    public async Task ADisabledCatalogueResolvesNothingEvenWithAValidToken()
    {
        // Answering as though the token were wrong, rather than "right token but switched off", is
        // the point: the controller turns null into a 404, and a disabled catalogue must not confirm
        // that it exists.
        var token = _db.SeedApiKey(_db.SeedUser(), UserApiKeyScope.Opds);
        _db.SetConfig();

        Assert.Null(await ResolveAsync(token));
    }

    [Fact]
    public async Task AValidTokenOnAnEnabledCatalogueResolvesToItsOwner()
    {
        var userId = _db.SeedUser();
        var token = _db.SeedApiKey(userId, UserApiKeyScope.Opds);
        EnableCatalogue(userId);

        var access = await ResolveAsync(token);

        Assert.NotNull(access);
        // Resolving to a *user* is what lets an OPDS read land on the right person's progress.
        Assert.Equal(userId, access.UserId);
        Assert.True(access.TrackProgress);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("deadbeef")]
    public async Task AnUnknownTokenIsRejected(string? provided)
    {
        var userId = _db.SeedUser();
        _db.SeedApiKey(userId, UserApiKeyScope.Opds);
        EnableCatalogue(userId);

        Assert.Null(await ResolveAsync(provided));
    }

    [Fact]
    public async Task TokenComparisonIsCaseSensitive()
    {
        // The token is generated and pasted, never typed, so there is no usability argument for
        // folding case — and the lookup is a digest match, where any transformation is a mismatch.
        var userId = _db.SeedUser();
        var token = _db.SeedApiKey(userId, UserApiKeyScope.Opds);
        EnableCatalogue(userId);

        Assert.Null(await ResolveAsync(token.ToUpperInvariant()));
    }

    [Fact]
    public async Task ARevokedTokenIsRejected()
    {
        var userId = _db.SeedUser();
        var token = _db.SeedApiKey(userId, UserApiKeyScope.Opds, revoked: true);
        EnableCatalogue(userId);

        Assert.Null(await ResolveAsync(token));
    }

    [Fact]
    public async Task AFullScopeApiKeyCannotBeUsedAsAnOpdsToken()
    {
        // The scopes exist precisely so the URL handed to a third-party reading app is not also a
        // credential for the management API. The converse must hold too.
        var userId = _db.SeedUser();
        var token = _db.SeedApiKey(userId, UserApiKeyScope.Full);
        EnableCatalogue(userId);

        Assert.Null(await ResolveAsync(token));
    }

    [Fact]
    public async Task ATokenBelongingToADisabledUserIsRejected()
    {
        var userId = _db.SeedUser(configure: u => u.Disabled = true);
        var token = _db.SeedApiKey(userId, UserApiKeyScope.Opds);
        EnableCatalogue(userId);

        // Suspending an account has to close its OPDS feed too, or the one credential that lives
        // outside the browser keeps working after the account is switched off.
        Assert.Null(await ResolveAsync(token));
    }

    [Fact]
    public async Task ATokenBelongingToAUserWithoutTheOpdsPermissionIsRejected()
    {
        var userId = _db.SeedUser(permissions: MakiPermission.UseTrackers);
        var token = _db.SeedApiKey(userId, UserApiKeyScope.Opds);
        EnableCatalogue(userId);

        Assert.Null(await ResolveAsync(token));
    }

    // ---- reading the settings ----

    [Fact]
    public async Task AnUnconfiguredInstanceResolvesNothing()
    {
        _db.SetConfig();

        Assert.Null(await ResolveAsync("anything"));
    }

    [Fact]
    public async Task ProgressTrackingIsOnUnlessExplicitlyDisabled()
    {
        var userId = _db.SeedUser();
        var token = _db.SeedApiKey(userId, UserApiKeyScope.Opds);

        // Absent means on; only an explicit "false" is the user having turned it off.
        _db.SetUserConfig(userId, (SettingKeys.OpdsEnabled, "true"));
        Assert.True((await ResolveAsync(token))!.TrackProgress);

        EnableCatalogue(userId, trackProgress: false);
        Assert.False((await ResolveAsync(token))!.TrackProgress);
    }

    // ---- the last-page-first guard ----

    [Fact]
    public void FetchingTheLastPageOfAnUnopenedChapterDoesNotCompleteIt()
    {
        // Several readers fetch the final page up front to size their page bar. Completion is
        // sticky and fires a tracker event, so there would be nothing to undo.
        Assert.False(OpdsProgressPolicy.CompletionFor(hasProgressRow: false, page: 9, pageCount: 10));
    }

    [Fact]
    public void FetchingTheLastPageOfAnOpenedChapterFollowsTheNormalRule()
    {
        Assert.Null(OpdsProgressPolicy.CompletionFor(hasProgressRow: true, page: 9, pageCount: 10));
    }

    [Fact]
    public void AnyEarlierPageFollowsTheNormalRule()
    {
        Assert.Null(OpdsProgressPolicy.CompletionFor(hasProgressRow: false, page: 0, pageCount: 10));
        Assert.Null(OpdsProgressPolicy.CompletionFor(hasProgressRow: false, page: 8, pageCount: 10));
    }

    [Fact]
    public void ASinglePageChapterIsAlsoCoveredByTheGuard()
    {
        // page 0 is both the first and the last page, so an unopened one-page chapter must not be
        // completed by the reader merely looking at it.
        Assert.False(OpdsProgressPolicy.CompletionFor(hasProgressRow: false, page: 0, pageCount: 1));
        Assert.Null(OpdsProgressPolicy.CompletionFor(hasProgressRow: true, page: 0, pageCount: 1));
    }
}
