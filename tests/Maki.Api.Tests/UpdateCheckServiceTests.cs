using Maki.Api.Services;

namespace Maki.Api.Tests;

public class UpdateCheckServiceTests
{
    [Theory]
    [InlineData("0.9.2", "0.9.1", true)]
    [InlineData("0.9.1", "0.9.1", false)]
    [InlineData("0.9.0", "0.9.1", false)]
    [InlineData("0.9.1", "0.9.1-beta.2", true)]
    [InlineData("0.9.1", "0.9.1-beta.2+abc", true)]
    [InlineData("0.9.1-beta.3", "0.9.1-beta.2", false)]
    [InlineData("0.9.2", "0.9.1-beta.2", true)]
    [InlineData("nonsense", "0.9.1", false)]
    public void IsNewer_compares_cores_and_offers_the_final_release_to_a_prerelease(
        string latest, string current, bool expected) =>
        Assert.Equal(expected, UpdateCheckService.IsNewer(latest, current));
}
