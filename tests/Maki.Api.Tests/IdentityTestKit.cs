using System.Security.Claims;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Maki.Api.Tests;

/// <summary>
/// A real <see cref="UserManager{TUser}"/> over a test database, plus a sign-in manager that never
/// needs an HTTP context, for controller tests that exercise Identity's own bookkeeping.
/// </summary>
internal static class IdentityTestKit
{
    public static UserManager<MakiUser> UserManager(MakiDbContext db, IdentityOptions? options = null)
    {
        var users = new UserManager<MakiUser>(
            new UserStore<MakiUser, IdentityRole<int>, MakiDbContext, int>(db),
            Options.Create(options ?? new IdentityOptions()),
            new PasswordHasher<MakiUser>(),
            [new UserValidator<MakiUser>()],
            [new PasswordValidator<MakiUser>()],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null!,
            NullLogger<UserManager<MakiUser>>.Instance);
        users.RegisterTokenProvider(TokenOptions.DefaultProvider, new AlwaysValidTokenProvider());
        return users;
    }

    public static string Hash(MakiUser user, string password) =>
        new PasswordHasher<MakiUser>().HashPassword(user, password);

    public static ClaimsPrincipal Principal(int userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test"));

    private sealed class AlwaysValidTokenProvider : IUserTwoFactorTokenProvider<MakiUser>
    {
        public Task<string> GenerateAsync(string purpose, UserManager<MakiUser> manager, MakiUser user) =>
            Task.FromResult("token");

        public Task<bool> ValidateAsync(string purpose, string token, UserManager<MakiUser> manager, MakiUser user) =>
            Task.FromResult(true);

        public Task<bool> CanGenerateTwoFactorTokenAsync(UserManager<MakiUser> manager, MakiUser user) =>
            Task.FromResult(false);
    }
}

/// <summary>
/// The real password check and lockout counting, with the cookie-writing parts stubbed out and the
/// two-factor cookie replaced by a user the test names.
/// </summary>
internal class TestSignInManager(UserManager<MakiUser> users, MakiUser? twoFactorUser = null)
    : SignInManager<MakiUser>(
        users,
        new Microsoft.AspNetCore.Http.HttpContextAccessor(),
        new UserClaimsPrincipalFactory<MakiUser>(users, Microsoft.Extensions.Options.Options.Create(new IdentityOptions())),
        Microsoft.Extensions.Options.Options.Create(new IdentityOptions()),
        NullLogger<SignInManager<MakiUser>>.Instance,
        null!,
        null!)
{
    public List<string> RedeemedRecoveryCodes { get; } = [];

    public override Task RefreshSignInAsync(MakiUser user) => Task.CompletedTask;

    public override Task SignInWithClaimsAsync(MakiUser user, bool isPersistent, IEnumerable<Claim> additionalClaims) =>
        Task.CompletedTask;

    public override Task<MakiUser?> GetTwoFactorAuthenticationUserAsync() => Task.FromResult(twoFactorUser);

    public override async Task<SignInResult> TwoFactorRecoveryCodeSignInAsync(string recoveryCode)
    {
        RedeemedRecoveryCodes.Add(recoveryCode);
        var redeemed = await UserManager.RedeemTwoFactorRecoveryCodeAsync(twoFactorUser!, recoveryCode);
        return redeemed.Succeeded ? SignInResult.Success : SignInResult.Failed;
    }
}
