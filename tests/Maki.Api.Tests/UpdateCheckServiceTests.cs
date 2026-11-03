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

    [Theory]
    [InlineData("https://github.com/OrbitMPGH/Maki/releases/tag/v0.35.0", "v0.35.0")]
    [InlineData("/OrbitMPGH/Maki/releases/tag/v0.35.0?x=1", "v0.35.0")]
    [InlineData("https://github.com/OrbitMPGH/Maki/releases/tag/v1.0.0-beta.1/", "v1.0.0-beta.1")]
    [InlineData("https://github.com/OrbitMPGH/Maki/releases", null)]
    [InlineData("https://github.com/OrbitMPGH/Maki/releases/tag/", null)]
    [InlineData(null, null)]
    public void TagFromReleaseLocation_reads_the_tag_out_of_the_latest_release_redirect(string? location, string? expected) =>
        Assert.Equal(expected, UpdateCheckService.TagFromReleaseLocation(
            location is null ? null : new Uri(location, UriKind.RelativeOrAbsolute)));
}
