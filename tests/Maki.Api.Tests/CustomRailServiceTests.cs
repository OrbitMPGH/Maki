using Maki.Api.Services;
using Maki.Core.Configuration;

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
}
