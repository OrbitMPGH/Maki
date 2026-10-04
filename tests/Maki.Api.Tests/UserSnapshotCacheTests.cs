using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Maki.Api.Auth;
using Maki.Api.Localization;
using Maki.Core.Security;
using Maki.Data;
using Maki.Data.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Maki.Api.Tests;

[Collection(ConfigDirCollection.Name)]
public class UserSnapshotCacheTests : IDisposable
{
    private readonly string _configDir;
    private readonly string? _previousConfigDir;

    public UserSnapshotCacheTests()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var api = Path.Combine(dir.FullName, "src", "Maki.Api");
            if (File.Exists(Path.Combine(api, "Maki.Api.csproj")))
            {
                Directory.CreateDirectory(Path.Combine(api, "wwwroot"));
                break;
            }
        }

        _configDir = Path.Combine(Path.GetTempPath(), "maki-snapshotcache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
        _previousConfigDir = Environment.GetEnvironmentVariable("MAKI_CONFIG_DIR");
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _configDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MAKI_CONFIG_DIR", _previousConfigDir);
        try
        {
            Directory.Delete(_configDir, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Second_request_within_ttl_does_not_query_the_users_table()
    {
        using var fixture = new TestDb();
        var userId = fixture.SeedUser("reader", MakiPermission.None, allRootFolders: false);
        var counter = new CommandCounter();
        var options = new DbContextOptionsBuilder<MakiDbContext>(fixture.Options).AddInterceptors(counter).Options;
        var cache = new UserSnapshotCache(new MemoryCache(new MemoryCacheOptions()));
        using var services = AuthServices();

        var first = await InvokeAsync(services, () => new MakiDbContext(options), cache, userId);
        Assert.True(first.Passed);
        Assert.Contains(counter.Commands, c => c.Contains("AspNetUsers"));
        Assert.Contains(counter.Commands, c => c.Contains("UserRootFolders"));

        counter.Commands.Clear();
        var second = await InvokeAsync(services, () => new MakiDbContext(options), cache, userId);
        Assert.True(second.Passed);
        Assert.Equal(userId, second.User.UserId);
        Assert.False(second.User.AllRootFolders);
        Assert.Empty(counter.Commands);

        await using (var db = fixture.NewContext())
        {
            await db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Disabled, true));
        }

        cache.Evict(userId);
        var third = await InvokeAsync(services, () => new MakiDbContext(options), cache, userId);
        Assert.False(third.Passed);
        Assert.Equal(StatusCodes.Status401Unauthorized, third.StatusCode);
    }

    [Fact]
    public async Task Disabled_user_is_rejected_on_the_next_request_after_the_admin_disables_it()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var cache = factory.Services.GetRequiredService<IUserSnapshotCache>();

        int readerId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();
            var reader = new MakiUser { UserName = "snapshot-reader", NormalizedUserName = "SNAPSHOT-READER", Permissions = MakiPermission.None, AllRootFolders = true };
            db.Users.Add(reader);
            await db.SaveChangesAsync();
            readerId = reader.Id;
        }

        // A browser session: user management refuses API keys.
        await CookieSession.SignInAsync(factory, client,
            new MakiUser { UserName = "snapshot-admin", Permissions = MakiPermission.Admin, AllRootFolders = true },
            "snapshot admin password");

        Assert.True((await InvokeInHostAsync(factory, cache, readerId)).Passed);
        Assert.NotNull(cache.Get(readerId));

        var disable = await client.PutAsJsonAsync($"/api/v1/users/{readerId}", new { disabled = true });
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        Assert.Null(cache.Get(readerId));

        var next = await InvokeInHostAsync(factory, cache, readerId);
        Assert.False(next.Passed);
        Assert.Equal(StatusCodes.Status401Unauthorized, next.StatusCode);
    }

    private static async Task<Outcome> InvokeInHostAsync(
        WebApplicationFactory<Program> factory, IUserSnapshotCache cache, int userId)
    {
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var current = sp.GetRequiredService<CurrentUserContext>();
        var passed = false;
        var context = Context(sp, userId);
        await new CurrentUserMiddleware(_ => { passed = true; return Task.CompletedTask; })
            .InvokeAsync(context, current, sp.GetRequiredService<DataScope>(),
                sp.GetRequiredService<MakiDbContext>(), sp.GetRequiredService<ILocalizer>(), cache);
        return new Outcome(passed, context.Response.StatusCode, current);
    }

    private static async Task<Outcome> InvokeAsync(
        ServiceProvider services, Func<MakiDbContext> newContext, IUserSnapshotCache cache, int userId)
    {
        await using var db = newContext();
        var current = new CurrentUserContext();
        var passed = false;
        var context = Context(services, userId);
        await new CurrentUserMiddleware(_ => { passed = true; return Task.CompletedTask; })
            .InvokeAsync(context, current, new DataScope(), db, new TestLocalizer(), cache);
        return new Outcome(passed, context.Response.StatusCode, current);
    }

    private static DefaultHttpContext Context(IServiceProvider services, int userId) => new()
    {
        RequestServices = services,
        User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "Test"))
    };

    private static ServiceProvider AuthServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication().AddCookie(IdentityConstants.ApplicationScheme);
        return services.BuildServiceProvider();
    }

    private sealed record Outcome(bool Passed, int StatusCode, CurrentUserContext User);

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands.Add(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
