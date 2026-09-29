using System.Text.Json;
using System.Text.Json.Serialization;
using Maki.Api.Auth;
using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Maki.Api.Tests;

/// <summary>
/// <c>JsonStringEnumConverter</c> (registered in <c>Program.cs</c>) deserializes an undefined
/// numeric value into an enum without complaint, so a request body of <c>{"scope": 7}</c> becomes a
/// <see cref="UserApiKeyScope"/> that names nothing. <see cref="AccountController.CreateApiKey"/> must
/// still refuse it, the same way it already refuses <see cref="UserApiKeyScope.Opds"/>.
/// </summary>
public class AccountApiKeyScopeTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private static readonly JsonSerializerOptions WireOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    private AccountController Controller(int userId)
    {
        var db = _db.NewContext(userId);
        var clock = new StoppedClock(new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero));
        return new AccountController(
            new TestLocalizer(), db, BuildUserManager(db), null!, new TestCurrentUser(userId),
            new AuthEventLogger(db, clock), new OidcRuntimeOptions(), clock,
            new UserSnapshotCache(new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions())));
    }

    private static UserManager<MakiUser> BuildUserManager(MakiDbContext db)
    {
        var store = new UserStore<MakiUser, IdentityRole<int>, MakiDbContext, int>(db);
        return new UserManager<MakiUser>(
            store,
            Options.Create(new IdentityOptions()),
            new PasswordHasher<MakiUser>(),
            [new UserValidator<MakiUser>()],
            [],
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            null!,
            NullLogger<UserManager<MakiUser>>.Instance);
    }

    private static string CodeOf(IActionResult result)
    {
        var body = Assert.IsType<BadRequestObjectResult>(result).Value!;
        return (string)body.GetType().GetProperty("code")!.GetValue(body)!;
    }

    [Fact]
    public void An_undefined_numeric_scope_deserializes_into_the_enum_without_error()
    {
        // Proves the premise: this is not a hypothetical, the wire format really does let "7"
        // through as a UserApiKeyScope with no name.
        var request = JsonSerializer.Deserialize<CreateApiKeyRequest>(
            """{"name":"a script","scope":7}""", WireOptions)!;

        Assert.False(Enum.IsDefined(request.Scope));
    }

    [Fact]
    public async Task An_undefined_numeric_scope_is_rejected()
    {
        var userId = _db.SeedUser("alice");
        var request = JsonSerializer.Deserialize<CreateApiKeyRequest>(
            """{"name":"a script","scope":7}""", WireOptions)!;

        var result = await Controller(userId).CreateApiKey(request, CancellationToken.None);

        Assert.Equal("error.account.invalidScope", CodeOf(result));
    }

    [Fact]
    public async Task The_opds_scope_still_gets_its_own_dedicated_message()
    {
        var userId = _db.SeedUser("alice");
        var request = new CreateApiKeyRequest("a script", UserApiKeyScope.Opds);

        var result = await Controller(userId).CreateApiKey(request, CancellationToken.None);

        Assert.Equal("error.account.opdsKeyOnOpdsCard", CodeOf(result));
    }

    [Fact]
    public async Task An_account_with_a_password_has_to_confirm_it_to_mint_a_key()
    {
        var userId = _db.SeedUser("alice", configure: u =>
            u.PasswordHash = new PasswordHasher<MakiUser>().HashPassword(u, "correct horse battery"));
        var request = new CreateApiKeyRequest("a script", UserApiKeyScope.Full);

        var result = await Controller(userId).CreateApiKey(request, CancellationToken.None);

        // A key outlives the session that minted it, so a hijacked session alone must not be enough.
        Assert.Equal("error.account.incorrectPassword", CodeOf(result));
    }

    [Fact]
    public async Task The_full_scope_is_accepted()
    {
        var userId = _db.SeedUser("alice");
        var request = new CreateApiKeyRequest("a script", UserApiKeyScope.Full);

        var result = await Controller(userId).CreateApiKey(request, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }
}
