using Maki.Api.Auth;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Maki.Api.Tests;

public sealed class LocalizedIdentityErrorTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Identity_validation_errors_come_from_the_catalogue()
    {
        _db.SeedUser("taken");
        var describer = new LocalizedIdentityErrorDescriber(new TestLocalizer());
        var options = new IdentityOptions();
        options.Password.RequiredLength = 10;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        using var db = _db.NewContext();
        using var users = new UserManager<MakiUser>(
            new UserStore<MakiUser, IdentityRole<int>, Maki.Data.MakiDbContext, int>(db),
            Options.Create(options),
            new PasswordHasher<MakiUser>(),
            [new UserValidator<MakiUser>(describer)],
            [new PasswordValidator<MakiUser>(describer)],
            new UpperInvariantLookupNormalizer(),
            describer,
            null!,
            NullLogger<UserManager<MakiUser>>.Instance);

        var shortPassword = await users.CreateAsync(new MakiUser { UserName = "fresh" }, "short");
        Assert.Equal("error.identity.passwordTooShort(length=10)", Assert.Single(shortPassword.Errors).Description);

        var duplicate = await users.CreateAsync(new MakiUser { UserName = "taken" }, "long enough password");
        Assert.Equal("error.identity.duplicateUserName(userName=taken)", Assert.Single(duplicate.Errors).Description);
    }
}
