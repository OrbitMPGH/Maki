using Maki.Api.Services;
using Maki.Core.Configuration;
using Microsoft.Extensions.Caching.Memory;

namespace Maki.Api.Tests;

/// <summary>
/// <see cref="CustomRailService.CountAsync"/>: Recommendations has no catalogue count to give (it
/// draws from the ranked pool, not a filtered scan), so it must answer null rather than falling
/// through to the catalogue's <c>DiscoverService.CountAsync</c> path, which needs dependencies this
/// test leaves null.
/// </summary>
public sealed class CustomRailServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private CustomRailService Service(int userId)
    {
        var db = _db.NewContext(userId);
        return new CustomRailService(db, new TestCurrentUser(userId), null!, null!, null!, null!, null!, null!);
    }

    [Fact]
    public async Task CountAsync_returns_null_for_a_recommendations_spec()
    {
        var alice = _db.SeedUser("alice");
        var spec = new CustomRailSpec(Source: CustomRailSources.Recommendations);

        var count = await Service(alice).CountAsync(spec, CancellationToken.None);

        Assert.Null(count);
    }

    [Fact]
    public async Task Library_rows_are_reused_within_the_window_and_not_across_users()
    {
        var alice = _db.SeedUser("alice");
        var bob = _db.SeedUser("bob");
        _db.SeedSeries("First");
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var spec = new CustomRailSpec(Source: CustomRailSources.Library);
        CustomRailService WithCache(int userId) => new(
            _db.NewContext(userId), new TestCurrentUser(userId), null!, null!, null!, null!, null!, null!, cache);

        var first = await WithCache(alice).CountAsync(spec, CancellationToken.None);
        _db.SeedSeries("Second");
        var reused = await WithCache(alice).CountAsync(spec, CancellationToken.None);
        var other = await WithCache(bob).CountAsync(spec, CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(1, reused);
        Assert.Equal(2, other);
    }
}
