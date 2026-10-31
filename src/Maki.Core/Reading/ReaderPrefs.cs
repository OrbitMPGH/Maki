using System.Text.Json;
using Maki.Core.Quality;

namespace Maki.Core.Reading;

/// <summary>
/// How the built-in reader displays a series. Stored as opaque JSON in two places: the user's
/// default in the <c>reader.prefs</c> user setting, and an optional per-series override on
/// <c>UserSeriesState.ReaderPrefsJson</c>.
/// <para>
/// Same discipline as <c>SavedFilter.Spec</c>: serialize only through <see cref="Json"/>, and
/// never rename or reorder a property. A name mismatch does not throw — it silently yields the
/// parameter default — so a renamed field degrades into "the user's setting was forgotten"
/// rather than an error. Adding a property is safe.
/// </para>
/// </summary>
public record ReaderPrefsSpec(
    string Mode = ReaderPrefsSpec.ModePaged,
    string Direction = ReaderPrefsSpec.DirectionRtl,
    string Fit = ReaderPrefsSpec.FitHeight,
    int PageGap = 0,
    int Preload = 3,
    bool TapZones = true,
    bool ShowPageNumber = true,
    bool AutoNextChapter = true,
    string Background = "#0a0a0b",
    /// <summary>
    /// Percent scale applied on top of <see cref="FitOriginal"/>. Only meaningful there: the other
    /// fit modes already size to the viewport, so scaling them too would fight that sizing instead
    /// of adding anything. 1:1 is exactly the mode this can't self-adjust for (a full-res vertical
    /// page is either too small to read or too big for the screen depending on source scan res),
    /// so it gets the one knob that lets a user correct it without leaving 1:1's pixel alignment.
    /// </summary>
    int Scale = 100,
    /// <summary>
    /// Flash the chapter's name over the page for a moment on entering it. Default on: end-of-chapter
    /// credit pages and the first pages of the next chapter often look alike, so without a cue the
    /// only evidence a chapter turn happened is the page counter resetting.
    /// </summary>
    bool ChapterBanner = true,
    /// <summary>Percent the page images are dimmed by, for reading in a dark room. 0 leaves them untouched.</summary>
    int Dim = 0,
    /// <summary>A colour filter applied to the page images: one of <c>none</c>, <c>grayscale</c>, <c>sepia</c>, <c>invert</c>.</summary>
    string Filter = ReaderPrefsSpec.FilterNone,
    /// <summary>Hold a screen wake lock while the reader is being read, where the browser offers one.</summary>
    bool KeepAwake = true,
    /// <summary>
    /// What single-page mode does with a page wider than it is tall: <c>off</c> shows it whole,
    /// <c>split</c> shows its halves one after the other in reading order, <c>rotate</c> turns it a
    /// quarter so it fills a portrait screen. Halves are a display concern only: progress still
    /// counts the archive's pages.
    /// </summary>
    string SplitWide = ReaderPrefsSpec.SplitOff,
    /// <summary>Trim uniform white or black margins off each page, measured in the browser.</summary>
    bool CropBorders = false,
    /// <summary>
    /// Open a chapter whose pages are mostly very tall in continuous mode. Null means "not set on
    /// this layer": the global defaults treat that as on, and a profile or series override as off,
    /// because choosing a layout there is the explicit choice the detection must not override.
    /// See <see cref="WithAutoWebtoon"/>.
    /// </summary>
    bool? AutoWebtoon = null,
    /// <summary>Sharpen low-resolution pages: <c>off</c>, <c>auto</c> (only when a page is shown larger than its source or measures soft in the browser) or <c>always</c>.</summary>
    string Enhance = ReaderPrefsSpec.EnhanceOff,
    /// <summary>
    /// How <see cref="Enhance"/> is done: a browser shader preset (<c>a4k-*</c>) or a server model
    /// (<c>server:*</c>). A server method is kept even when the server tier is off, so the choice
    /// survives an admin toggling it; the client decides the fallback.
    /// </summary>
    string EnhanceMethod = ReaderPrefsSpec.EnhanceMethodDefault,
    /// <summary>
    /// The browser preset a server <see cref="EnhanceMethod"/> shows until its upscale is ready, and
    /// whenever the server tier cannot be used: an <c>a4k-*</c> preset, or <c>off</c> for the plain page.
    /// </summary>
    string EnhanceFallback = ReaderPrefsSpec.EnhanceMethodDefault,
    /// <summary>
    /// Quality tiers (<c>QualityTier</c> names in lower case) whose chapters are never enhanced,
    /// whatever <see cref="Enhance"/> says. Null means unset and becomes <see cref="DefaultEnhanceSkipTiers"/>:
    /// a digital volume is already clean, and sharpening it only adds artefacts.
    /// </summary>
    IReadOnlyList<string>? EnhanceSkipTiers = null)
{
    public const string ModePaged = "paged";
    public const string ModeDouble = "double";
    public const string ModeVertical = "vertical";

    public const string DirectionLtr = "ltr";

    /// <summary>
    /// Right-to-left is the default: manga, and a series with no type, is tagged
    /// <c>Manga = "YesAndRightToLeft"</c> in its ComicInfo. Manhwa and manhua are tagged
    /// <c>Yes</c> and want vertical + left-to-right, which is what the per-series override and the
    /// type-matched reading profiles are for.
    /// </summary>
    public const string DirectionRtl = "rtl";

    public const string FilterNone = "none";

    public const string SplitOff = "off";

    public const string EnhanceOff = "off";
    public const string EnhanceMethodDefault = "a4k-m";

    public const string FitWidth = "width";
    public const string FitHeight = "height";
    public const string FitScreen = "screen";
    public const string FitOriginal = "original";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly string[] Modes = [ModePaged, ModeDouble, ModeVertical];
    private static readonly string[] Directions = [DirectionLtr, DirectionRtl];
    private static readonly string[] Fits = [FitWidth, FitHeight, FitScreen, FitOriginal];
    private static readonly string[] Filters = [FilterNone, "grayscale", "sepia", "invert"];
    private static readonly string[] SplitModes = [SplitOff, "split", "rotate"];
    private static readonly string[] EnhanceModes = [EnhanceOff, "auto", "always"];
    private static readonly string[] EnhanceMethods =
        ["a4k-s", EnhanceMethodDefault, "a4k-a", "server:span-s", "server:realesrgan-av3"];
    private static readonly string[] EnhanceFallbacks = ["a4k-s", EnhanceMethodDefault, "a4k-a", EnhanceOff];
    private static readonly string[] Tiers = [.. Enum.GetValues<QualityTier>().Select(QualitySnapshot.TierName)];

    public static readonly IReadOnlyList<string> DefaultEnhanceSkipTiers = ["volume"];

    /// <summary>Clamps free-text fields back onto known values so a bad write can't wedge the reader.</summary>
    public ReaderPrefsSpec Sanitized() => this with
    {
        Mode = Modes.Contains(Mode) ? Mode : ModePaged,
        Direction = Directions.Contains(Direction) ? Direction : DirectionRtl,
        Fit = Fits.Contains(Fit) ? Fit : FitHeight,
        PageGap = Math.Clamp(PageGap, 0, 64),
        Preload = Math.Clamp(Preload, 0, 10),
        Scale = Math.Clamp(Scale, 25, 400),
        Dim = Math.Clamp(Dim, 0, 80),
        Filter = Filters.Contains(Filter) ? Filter : FilterNone,
        SplitWide = SplitModes.Contains(SplitWide) ? SplitWide : SplitOff,
        Enhance = EnhanceModes.Contains(Enhance) ? Enhance : EnhanceOff,
        EnhanceMethod = EnhanceMethods.Contains(EnhanceMethod) ? EnhanceMethod : EnhanceMethodDefault,
        EnhanceFallback = EnhanceFallbacks.Contains(EnhanceFallback) ? EnhanceFallback : EnhanceMethodDefault,
        EnhanceSkipTiers = EnhanceSkipTiers is null
            ? DefaultEnhanceSkipTiers
            : [.. Tiers.Where(EnhanceSkipTiers.Contains)],
    };

    /// <summary>Fills an unset <see cref="AutoWebtoon"/> with the layer's default: on for the global defaults only.</summary>
    public ReaderPrefsSpec WithAutoWebtoon(bool globalLayer) => this with { AutoWebtoon = AutoWebtoon ?? globalLayer };

    /// <summary>Reads a stored blob, falling back to defaults for null/blank/unparseable JSON.</summary>
    public static ReaderPrefsSpec Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ReaderPrefsSpec().Sanitized();
        }

        try
        {
            return (JsonSerializer.Deserialize<ReaderPrefsSpec>(json, Json) ?? new ReaderPrefsSpec()).Sanitized();
        }
        catch (JsonException)
        {
            return new ReaderPrefsSpec().Sanitized();
        }
    }

    public static string Serialize(ReaderPrefsSpec spec) => JsonSerializer.Serialize(spec.Sanitized(), Json);
}
