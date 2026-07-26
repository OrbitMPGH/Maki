using Maki.Core.Configuration;

namespace Maki.Core.Tests;

/// <summary>
/// The start-page setting's validation. Lives in Core rather than being inlined in
/// <c>SettingsController</c> precisely so it can be tested without standing up that controller's
/// ~18 dependencies.
/// </summary>
public class StartPageTests
{
    [Theory]
    [InlineData("home")]
    [InlineData("library")]
    [InlineData("discover")]
    public void IsValid_accepts_known_pages(string page) => Assert.True(StartPage.IsValid(page));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Home")] // stored values are lowercase; a case mismatch is not a silent pass
    [InlineData("rewind")]
    public void IsValid_rejects_everything_else(string? page) => Assert.False(StartPage.IsValid(page));

    [Fact]
    public void Default_is_home() => Assert.Equal(StartPage.Home, StartPage.Default);
}
