namespace Maki.Sources.GigaViewer;

/// <summary>
/// One GigaViewer-powered site. GigaViewer is Hatena's white-label manga viewer, reused as-is
/// (same markup, same API shape) across several publishers under their own domain and branding.
/// </summary>
public record GigaViewerSite(string Name, string DisplayName, string BaseUrl);

public static class GigaViewerSites
{
    public static readonly GigaViewerSite ShonenJumpPlus =
        new("shonenjumpplus", "Shonen Jump+", "https://shonenjumpplus.com");

    public static readonly GigaViewerSite ComicDays =
        new("comicdays", "Comic Days", "https://comic-days.com");

    public static readonly GigaViewerSite SundayWebry =
        new("sundaywebry", "Sunday Webry", "https://www.sunday-webry.com");

    public static readonly GigaViewerSite Magcomi =
        new("magcomi", "MAGCOMI", "https://magcomi.com");

    public static readonly GigaViewerSite TonarinoYj =
        new("tonarinoyj", "Tonari no Young Jump", "https://tonarinoyj.jp");

    public static readonly GigaViewerSite ComicZenon =
        new("comiczenon", "Comic Zenon", "https://comic-zenon.com");

    public static readonly GigaViewerSite KurageBunch =
        new("kuragebunch", "Kurage Bunch", "https://kuragebunch.com");
}
