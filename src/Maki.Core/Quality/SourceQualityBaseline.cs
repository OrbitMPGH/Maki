namespace Maki.Core.Quality;

/// <summary>How a source's copies measure in general, as opposed to on one series.</summary>
public enum SourceQualityRating
{
    Low,
    Fair,
    Good,
    High
}

/// <summary>
/// Library-wide quality per source, before this instance has measured anything of its own. Medians
/// of JPG-equivalent bits per pixel (<see cref="MeasuredQuality"/>) from sampling a 3,900 file
/// English library in September 2026. Only sources that library had enough pages from are listed;
/// the rest are simply unknown, not assumed average.
/// </summary>
public static class SourceQualityBaseline
{
    public static IReadOnlyDictionary<string, double> BitsPerPixel { get; } =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["mangadex"] = 2.6,
            ["webtoons"] = 2.15,
            ["mangapill"] = 1.73,
            ["asura"] = 1.13,
            ["weebcentral"] = 0.92,
            ["mangafire"] = 0.85,
            ["atsumaru"] = 0.71,
            ["topmanhua"] = 0.66
        };

    /// <summary>
    /// Default for <c>SettingKeys.SourcePriorityOrder</c> when it was never set: the sources above
    /// best first, official sources lifted to the top since they rank first in any quality profile,
    /// and the unmeasured English sources left where registration order put them.
    /// </summary>
    public const string DefaultPriorityOrder =
        "mangadex,mangaplus,webtoons,tcbscans,asura,flamecomics,mangapill,weebcentral,mangafire,mangakatana,mangakakalot,atsumaru,topmanhua";

    public static SourceQualityRating Rate(double jpgEquivalentBitsPerPixel) => jpgEquivalentBitsPerPixel switch
    {
        >= 2.0 => SourceQualityRating.High,
        >= 1.2 => SourceQualityRating.Good,
        >= 0.8 => SourceQualityRating.Fair,
        _ => SourceQualityRating.Low
    };
}
