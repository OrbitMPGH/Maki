using System.Security.Claims;
using Maki.Api.Auth;
using Maki.Core.Configuration;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Maki.Api.Tests;

/// <summary>
/// Single sign-on: what the settings mean, what a provider's claims are allowed to decide, and which
/// account a subject resolves to. Everything here is a decision about who somebody is or what they
/// may do, so none of it is left to an end-to-end pass against a live provider.
/// </summary>
public class OidcTests
{
    // ---- settings and their gates ----

    [Fact]
    public async Task SingleSignOnIsNotEnabledUntilItIsAlsoConfigured()
    {
        using var fixture = new TestDb();
        fixture.SetConfig((SettingKeys.AuthOidcEnabled, "true"));

        var options = await LoadAsync(fixture);

        // Switched on with no issuer and no client id. A button here would lead only to a discovery
        // failure the user cannot read.
        Assert.False(options.Enabled);
    }

    [Fact]
    public async Task RequiringSingleSignOnDoesNothingWhileSingleSignOnIsUnusable()
    {
        using var fixture = new TestDb();
        fixture.SetConfig(
            (SettingKeys.AuthOidcEnabled, "true"),
            (SettingKeys.AuthOidcOnly, "true"));

        var options = await LoadAsync(fixture);

        // The failure this prevents: switch both on, get the issuer wrong, and every non-admin is
        // locked out of an instance whose only other way in does not work either.
        Assert.False(options.OidcOnly);
    }

    [Fact]
    public async Task TheBreakGlassVariableRestoresPasswordLoginForEveryone()
    {
        using var fixture = new TestDb();
        fixture.SetConfig(
            (SettingKeys.AuthOidcEnabled, "true"),
            (SettingKeys.AuthOidcAuthority, "https://auth.example.com"),
            (SettingKeys.AuthOidcClientId, "maki"),
            (SettingKeys.AuthOidcOnly, "true"));

        var options = await LoadAsync(fixture);
        Assert.True(options.OidcOnly);

        Environment.SetEnvironmentVariable(OidcRuntimeOptions.BreakGlassVariable, "1");
        try
        {
            // Read per call rather than captured at load, so setting the variable and restarting is
            // the whole recovery procedure for a provider that has stopped answering.
            Assert.False(options.OidcOnly);
        }
        finally
        {
            Environment.SetEnvironmentVariable(OidcRuntimeOptions.BreakGlassVariable, null);
        }
    }

    [Fact]
    public async Task TheAuthorityLosesItsTrailingSlash()
    {
        using var fixture = new TestDb();
        fixture.SetConfig(
            (SettingKeys.AuthOidcEnabled, "true"),
            (SettingKeys.AuthOidcAuthority, "https://auth.example.com/realms/maki/"),
            (SettingKeys.AuthOidcClientId, "maki"));

        var options = await LoadAsync(fixture);

        // The handler builds the discovery URL by concatenation, and a doubled slash is a 404 on
        // several providers.
        Assert.Equal("https://auth.example.com/realms/maki", options.Authority);
    }

    [Theory]
    [InlineData("profile email", new[] { "profile", "email" })]
    [InlineData("profile, email, groups", new[] { "profile", "email", "groups" })]
    // openid is dropped because the handler adds it unconditionally, and some providers reject a
    // request that lists it twice.
    [InlineData("openid profile", new[] { "profile" })]
    [InlineData("", new[] { "profile", "email" })]
    public void ScopesAcceptEitherSeparatorAndNeverRepeatOpenid(string raw, string[] expected) =>
        Assert.Equal(expected, OidcRuntimeOptions.ParseScopes(raw));

    [Fact]
    public void AClaimRuleWithNoValueMatchesAnyValue()
    {
        var rule = ClaimRule.Parse("groups");

        Assert.NotNull(rule);
        Assert.Equal("groups", rule.Name);
        Assert.Null(rule.Value);
        Assert.True(rule.IsSatisfiedBy([new Claim("groups", "anything")]));
        Assert.False(rule.IsSatisfiedBy([new Claim("roles", "anything")]));
    }

    [Fact]
    public void AClaimRuleSplitsOnTheFirstEqualsOnly()
    {
        // Claim values routinely contain one — a group distinguished name, a URL.
        var rule = ClaimRule.Parse("groups=cn=admins,ou=groups");

        Assert.NotNull(rule);
        Assert.Equal("groups", rule.Name);
        Assert.Equal("cn=admins,ou=groups", rule.Value);
        Assert.True(rule.IsSatisfiedBy([new Claim("groups", "cn=admins,ou=groups")]));
        Assert.False(rule.IsSatisfiedBy([new Claim("groups", "cn=readers,ou=groups")]));
    }

    // ---- claim mapping ----

    [Fact]
    public async Task WithNoClaimMappingConfiguredTheProviderSaysNothingAboutPermissions()
    {
        var options = await OptionsAsync();

        var mapped = OidcClaimMapper.Map(
            options, [new Claim("groups", "Admin")], MakiPermission.UseOpds);

        // The common case: single sign-on replaces the password and Maki's own user list still
        // decides what anyone may do. A "groups" claim full of unrelated values must not move it.
        Assert.Equal(MakiPermission.UseOpds, mapped);
    }

    [Fact]
    public async Task TheAdminClaimGrantsAdminAndNothingElseIsStored()
    {
        var options = await OptionsAsync(
            (SettingKeys.AuthOidcAdminClaim, "groups=maki-admins"));

        var mapped = OidcClaimMapper.Map(
            options, [new Claim("groups", "maki-admins")], MakiPermission.None);

        // Admin implies every other permission, so it is stored on its own — the shape the user
        // editor produces and the one MakiPermissions.Grants is written against.
        Assert.Equal(MakiPermission.Admin, mapped);
        Assert.True(mapped.Grants(MakiPermission.DownloadChapters));
    }

    [Fact]
    public async Task ThePermissionClaimCannotGrantAdmin()
    {
        var options = await OptionsAsync(
            (SettingKeys.AuthOidcPermissionClaim, "groups"),
            (SettingKeys.AuthOidcAdminClaim, "groups=maki-admins"));

        var mapped = OidcClaimMapper.Map(
            options, [new Claim("groups", "Admin"), new Claim("groups", "DownloadChapters")],
            MakiPermission.None);

        // Otherwise any provider whose group names happen to include "Admin" hands over the whole
        // instance, and the operator who configured a separate admin claim has no way to say
        // otherwise.
        Assert.False((mapped & MakiPermission.Admin) != 0);
        Assert.True(mapped.Grants(MakiPermission.DownloadChapters));
    }

    [Theory]
    // Enum.TryParse accepts a comma-separated list and a raw number for a [Flags] enum, so each of
    // these parses to a composite that is *not equal* to MakiPermission.Admin while still carrying
    // its bit. An equality guard passes them straight through.
    [InlineData("Admin,AddSeries")]
    [InlineData("admin, downloadchapters")]
    [InlineData("3")]
    [InlineData("5")]
    [InlineData("2047")]
    public async Task ACompositeOrNumericClaimValueCannotSmuggleTheAdminBit(string value)
    {
        var options = await OptionsAsync((SettingKeys.AuthOidcPermissionClaim, "groups"));

        var mapped = OidcClaimMapper.Map(options, [new Claim("groups", value)], MakiPermission.None);

        // The numeric forms are the dangerous ones in practice: providers that emit numeric group
        // ids (POSIX gids, GitLab group ids) would hand Admin to every user in an odd-numbered
        // group, on every sign-in, with no group anywhere named "Admin".
        Assert.Equal(MakiPermission.None, mapped & MakiPermission.Admin);
        Assert.False(mapped.Grants(MakiPermission.Admin));
    }

    [Fact]
    public async Task ValuesThatNameNoPermissionAreIgnoredRatherThanRejected()
    {
        var options = await OptionsAsync((SettingKeys.AuthOidcPermissionClaim, "groups"));

        var mapped = OidcClaimMapper.Map(
            options,
            [
                new Claim("groups", "engineering"),
                new Claim("groups", "useopds"),
                new Claim("groups", "vpn-users")
            ],
            MakiPermission.None);

        // A real groups claim is mostly values that have nothing to do with Maki; every one of them
        // has to be inert. Matching is case-insensitive because no directory writes them this way.
        Assert.Equal(MakiPermission.UseOpds, mapped);
    }

    [Fact]
    public async Task ConfiguringAMappingMakesTheProviderTheAuthority()
    {
        var options = await OptionsAsync((SettingKeys.AuthOidcPermissionClaim, "groups"));

        // Held locally, no longer claimed upstream: the permission goes away. That is the point of
        // configuring the mapping, and the reason the settings card warns that local edits will not
        // survive the next sign-in.
        var mapped = OidcClaimMapper.Map(options, [], MakiPermission.DownloadChapters);

        Assert.Equal(MakiPermission.None, mapped);
    }

    [Fact]
    public async Task AProvisionedUserNameStripsWhatIdentityWouldReject()
    {
        var options = await OptionsAsync();

        var name = OidcClaimMapper.UserName(
            options, [new Claim("preferred_username", "ada lovel!ace")], "sub-1");

        // Identity's default validator allows letters, digits and -._@+ only, and CreateAsync would
        // otherwise fail with a message about characters the user never typed.
        Assert.Equal("adalovelace", name);
    }

    [Fact]
    public async Task AUserNameFallsBackToTheSubjectWhenNothingUsableIsClaimed()
    {
        var options = await OptionsAsync();

        Assert.Equal("sub-1", OidcClaimMapper.UserName(options, [new Claim("groups", "x")], "sub-1"));
        Assert.Equal("sub-1", OidcClaimMapper.UserName(options, [new Claim("preferred_username", "!!!")], "sub-1"));
    }

    // ---- resolving a subject to an account ----

    [Fact]
    public async Task AnUnknownSubjectIsRefusedWhileAutoProvisioningIsOff()
    {
        using var fixture = new TestDb();
        var service = await ServiceAsync(fixture);

        var result = await service.SignInAsync("oidc", "sub-1", Claims(), default);

        Assert.Null(result.User);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task AProvisionedAccountStartsWithNoLibraryAccess()
    {
        using var fixture = new TestDb();
        var service = await ServiceAsync(fixture, (SettingKeys.AuthOidcAutoProvision, "true"));

        var result = await service.SignInAsync(
            "oidc", "sub-1", Claims(("preferred_username", "ada"), ("email", "ada@example.com")), default);

        Assert.NotNull(result.User);
        Assert.True(result.Provisioned);
        Assert.Equal("ada", result.User.UserName);
        // Fail closed, exactly as a hand-created account does. The provider says who somebody is,
        // never what they may read.
        Assert.False(result.User.AllRootFolders);
        Assert.Equal(MakiPermissions.DefaultForNewUser, result.User.Permissions);
        // Not the unclaimed placeholder: this account is real, it simply has no password.
        Assert.False(result.User.PendingSetup);
        Assert.Null(result.User.PasswordHash);
    }

    [Fact]
    public async Task TheSameSubjectComesBackToTheSameAccount()
    {
        using var fixture = new TestDb();
        var service = await ServiceAsync(fixture, (SettingKeys.AuthOidcAutoProvision, "true"));

        var first = await service.SignInAsync("oidc", "sub-1", Claims(("preferred_username", "ada")), default);

        // A second sign-in with a *different* username claim: the durable link is the subject, so
        // renaming somebody upstream must not strand them with a second empty library.
        var second = await ServiceAsync(fixture, (SettingKeys.AuthOidcAutoProvision, "true"));
        var again = await second.SignInAsync("oidc", "sub-1", Claims(("preferred_username", "ada-lovelace")), default);

        Assert.NotNull(first.User);
        Assert.NotNull(again.User);
        Assert.Equal(first.User.Id, again.User.Id);
        Assert.False(again.Provisioned);

        using var db = fixture.NewContext();
        Assert.Single(db.Users.Where(u => u.UserName != "owner"));
    }

    [Fact]
    public async Task AVerifiedEmailLinksToAnExistingLocalAccount()
    {
        using var fixture = new TestDb();
        var existing = fixture.SeedUser("ada", MakiPermission.UseOpds, configure: u =>
        {
            u.Email = "ada@example.com";
            u.NormalizedEmail = "ADA@EXAMPLE.COM";
        });

        var service = await ServiceAsync(fixture);

        var result = await service.SignInAsync(
            "oidc", "sub-1",
            Claims(("email", "ada@example.com"), ("email_verified", "true")),
            default);

        // The upgrade path: an instance whose users already have passwords should not have to
        // abandon their reading history to move to single sign-on.
        Assert.NotNull(result.User);
        Assert.Equal(existing, result.User.Id);
        Assert.True(result.Linked);
        Assert.False(result.Provisioned);
    }

    [Fact]
    public async Task AnUnverifiedEmailLinksToNothing()
    {
        using var fixture = new TestDb();
        fixture.SeedUser("ada", configure: u =>
        {
            u.Email = "ada@example.com";
            u.NormalizedEmail = "ADA@EXAMPLE.COM";
        });

        var service = await ServiceAsync(fixture);

        var result = await service.SignInAsync(
            "oidc", "sub-1", Claims(("email", "ada@example.com")), default);

        // Whenever a provider lets a user set an arbitrary unverified address, matching on it is
        // account takeover: claim the admin's address, sign in, inherit the library.
        Assert.Null(result.User);
    }

    [Fact]
    public async Task AnAmbiguousEmailLinksToNothing()
    {
        using var fixture = new TestDb();
        foreach (var name in new[] { "ada", "grace" })
        {
            fixture.SeedUser(name, configure: u =>
            {
                u.Email = "shared@example.com";
                u.NormalizedEmail = "SHARED@EXAMPLE.COM";
            });
        }

        var service = await ServiceAsync(fixture);

        var result = await service.SignInAsync(
            "oidc", "sub-1",
            Claims(("email", "shared@example.com"), ("email_verified", "true")),
            default);

        // Email is not unique in this schema (there is no mail server to confirm one against), so
        // two accounts may legitimately share an address. Linking to a guess is a coin flip over
        // whose library the caller walks into.
        Assert.Null(result.User);
    }

    [Fact]
    public async Task TheUnclaimedPlaceholderCannotBeTakenOverThroughSingleSignOn()
    {
        using var fixture = new TestDb();
        fixture.SeedUser("admin", MakiPermission.Admin, configure: u =>
        {
            u.PendingSetup = true;
            u.Email = "admin@example.com";
            u.NormalizedEmail = "ADMIN@EXAMPLE.COM";
        });

        var service = await ServiceAsync(fixture, (SettingKeys.AuthOidcAutoProvision, "true"));

        var result = await service.SignInAsync(
            "oidc", "sub-1",
            Claims(("email", "admin@example.com"), ("email_verified", "true"), ("preferred_username", "admin")),
            default);

        // That row owns the entire pre-upgrade library. Only POST auth/setup may claim it — otherwise
        // the first person the provider will authenticate walks into somebody else's library as an
        // admin. Here the name is taken too, so provisioning is refused as well.
        Assert.Null(result.User);
    }

    [Fact]
    public async Task ADisabledAccountIsStillRefusedAfterAValidProviderLogin()
    {
        using var fixture = new TestDb();
        var service = await ServiceAsync(fixture, (SettingKeys.AuthOidcAutoProvision, "true"));
        var created = await service.SignInAsync("oidc", "sub-1", Claims(("preferred_username", "ada")), default);
        Assert.NotNull(created.User);

        using (var db = fixture.NewContext())
        {
            var row = db.Users.Single(u => u.Id == created.User.Id);
            row.Disabled = true;
            db.SaveChanges();
        }

        var again = await ServiceAsync(fixture, (SettingKeys.AuthOidcAutoProvision, "true"));
        var result = await again.SignInAsync("oidc", "sub-1", Claims(("preferred_username", "ada")), default);

        Assert.Null(result.User);
    }

    [Fact]
    public async Task ANameCollisionIsRefusedRatherThanLinkedOrSuffixed()
    {
        using var fixture = new TestDb();
        fixture.SeedUser("ada");

        var service = await ServiceAsync(fixture, (SettingKeys.AuthOidcAutoProvision, "true"));

        var result = await service.SignInAsync(
            "oidc", "sub-1", Claims(("preferred_username", "ada")), default);

        // Linking would hand a new subject an existing person's library; a silent "ada2" is a support
        // question nobody can answer six months later.
        Assert.Null(result.User);
        Assert.Contains("already exists", result.Error);
    }

    [Fact]
    public async Task PermissionsAreReappliedOnEverySignInOnceAMappingIsConfigured()
    {
        using var fixture = new TestDb();
        var service = await ServiceAsync(fixture,
            (SettingKeys.AuthOidcAutoProvision, "true"),
            (SettingKeys.AuthOidcPermissionClaim, "groups"));

        var first = await service.SignInAsync("oidc", "sub-1",
            Claims(("preferred_username", "ada"), ("groups", "DownloadChapters")), default);
        Assert.NotNull(first.User);
        Assert.True(first.User.Permissions.Grants(MakiPermission.DownloadChapters));

        var second = await ServiceAsync(fixture,
            (SettingKeys.AuthOidcAutoProvision, "true"),
            (SettingKeys.AuthOidcPermissionClaim, "groups"));
        var again = await second.SignInAsync("oidc", "sub-1", Claims(("preferred_username", "ada")), default);

        // Dropped from the group upstream, so the permission goes here too — on the next sign-in, and
        // persisted, not merely computed for the request.
        Assert.NotNull(again.User);
        Assert.False(again.User.Permissions.Grants(MakiPermission.DownloadChapters));

        using var db = fixture.NewContext();
        Assert.False(db.Users.Single(u => u.Id == again.User.Id).Permissions
            .Grants(MakiPermission.DownloadChapters));
    }

    [Fact]
    public async Task PermissionsAreLeftAloneWhenNoMappingIsConfigured()
    {
        using var fixture = new TestDb();
        var service = await ServiceAsync(fixture, (SettingKeys.AuthOidcAutoProvision, "true"));
        var created = await service.SignInAsync("oidc", "sub-1", Claims(("preferred_username", "ada")), default);
        Assert.NotNull(created.User);

        using (var db = fixture.NewContext())
        {
            var row = db.Users.Single(u => u.Id == created.User.Id);
            row.Permissions = MakiPermission.DownloadChapters | MakiPermission.UseOpds;
            db.SaveChanges();
        }

        var again = await ServiceAsync(fixture, (SettingKeys.AuthOidcAutoProvision, "true"));
        var result = await again.SignInAsync("oidc", "sub-1", Claims(("preferred_username", "ada")), default);

        // An admin's edit on the Users page has to survive the next sign-in when the provider was
        // never made the authority on permissions.
        Assert.NotNull(result.User);
        Assert.True(result.User.Permissions.Grants(MakiPermission.DownloadChapters));
    }

    // ---- fixture plumbing ----

    private static Claim[] Claims(params (string Type, string Value)[] claims) =>
        claims.Select(c => new Claim(c.Type, c.Value)).ToArray();

    private static async Task<OidcRuntimeOptions> LoadAsync(TestDb fixture)
    {
        var options = new OidcRuntimeOptions();
        using var db = fixture.NewContext();
        await options.LoadAsync(db);
        return options;
    }

    private static async Task<OidcRuntimeOptions> OptionsAsync(params (string Key, string Value)[] settings)
    {
        using var fixture = new TestDb();
        fixture.SetConfig([
            (SettingKeys.AuthOidcEnabled, "true"),
            (SettingKeys.AuthOidcAuthority, "https://auth.example.com"),
            (SettingKeys.AuthOidcClientId, "maki"),
            .. settings
        ]);
        return await LoadAsync(fixture);
    }

    /// <summary>
    /// A real <see cref="UserManager{TUser}"/> over the fixture's database rather than a fake: the
    /// behaviour under test is mostly what Identity does with <c>AspNetUserLogins</c>, normalized
    /// names and validation, and a fake would only assert that the test's own idea of Identity is
    /// self-consistent.
    /// </summary>
    private static async Task<OidcSignInService> ServiceAsync(
        TestDb fixture, params (string Key, string Value)[] settings)
    {
        var options = new OidcRuntimeOptions();
        var db = fixture.NewContext();

        if (settings.Length > 0)
        {
            fixture.SetConfig([
                (SettingKeys.AuthOidcEnabled, "true"),
                (SettingKeys.AuthOidcAuthority, "https://auth.example.com"),
                (SettingKeys.AuthOidcClientId, "maki"),
                .. settings
            ]);
        }

        await options.LoadAsync(db);

        var store = new UserStore<MakiUser, IdentityRole<int>, MakiDbContext, int>(db);
        var userManager = new UserManager<MakiUser>(
            store,
            Options.Create(new IdentityOptions()),
            new PasswordHasher<MakiUser>(),
            [new UserValidator<MakiUser>()],
            [],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null!,
            NullLogger<UserManager<MakiUser>>.Instance);

        return new OidcSignInService(
            db, userManager, options,
            new StoppedClock(new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero)),
            NullLogger<OidcSignInService>.Instance);
    }
}
