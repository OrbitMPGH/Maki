using System.Text.Json;

namespace Maki.Core.Reading;

/// <summary>
/// How the built-in reader displays a series. Stored as opaque JSON in two places: one global
/// default in AppConfig, and an optional per-series override on <c>Series.ReaderPrefsJson</c>.
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
    bool SplitWidePages = false,
    bool AutoNextChapter = true,
    string Background = "#0a0a0b")
{
    public const string ModePaged = "paged";
    public const string ModeDouble = "double";
    public const string ModeVertical = "vertical";

    public const string DirectionLtr = "ltr";

    /// <summary>
    /// Right-to-left is the default: everything Maki packages is tagged
    /// <c>Manga = "YesAndRightToLeft"</c> in its ComicInfo. Manhwa and manhua want vertical +
    /// left-to-right, which is exactly what the per-series override is for.
    /// </summary>
    public const string DirectionRtl = "rtl";

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

    /// <summary>Clamps free-text fields back onto known values so a bad write can't wedge the reader.</summary>
    public ReaderPrefsSpec Sanitized() => this with
    {
        Mode = Modes.Contains(Mode) ? Mode : ModePaged,
        Direction = Directions.Contains(Direction) ? Direction : DirectionRtl,
        Fit = Fits.Contains(Fit) ? Fit : FitHeight,
        PageGap = Math.Clamp(PageGap, 0, 64),
        Preload = Math.Clamp(Preload, 0, 10),
    };

    /// <summary>Reads a stored blob, falling back to defaults for null/blank/unparseable JSON.</summary>
    public static ReaderPrefsSpec Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ReaderPrefsSpec();
        }

        try
        {
            return (JsonSerializer.Deserialize<ReaderPrefsSpec>(json, Json) ?? new ReaderPrefsSpec()).Sanitized();
        }
        catch (JsonException)
        {
            return new ReaderPrefsSpec();
        }
    }

    public static string Serialize(ReaderPrefsSpec spec) => JsonSerializer.Serialize(spec.Sanitized(), Json);
}
