namespace Maki.Core.Configuration;

/// <summary>
/// Which page the web UI opens on. Stored server-side so it follows the user across devices,
/// unlike the theme, which is per-browser.
/// </summary>
public static class StartPage
{
    /// <summary>The dashboard: continue reading, recently added, in-flight downloads. Default.</summary>
    public const string Home = "home";

    public const string Library = "library";

    /// <summary>
    /// Needs the local MangaBaka database. The client falls back to <see cref="Home"/> when it
    /// isn't installed — see <c>SettingKeys.UiStartPage</c> for why that fallback is load-bearing.
    /// </summary>
    public const string Discover = "discover";

    public const string Default = Home;

    public static readonly string[] All = [Home, Library, Discover];

    public static bool IsValid(string? page) => page is not null && All.Contains(page);
}
