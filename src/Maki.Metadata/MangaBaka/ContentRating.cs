using Maki.Core.Configuration;

namespace Maki.Metadata.MangaBaka;

/// <summary>
/// MangaBaka's <c>content_rating</c> vocabulary, ordered least to most explicit. The Discover
/// setting stores a single ceiling rating; everything at or below it in this order is shown.
/// </summary>
public static class ContentRating
{
    public const string Safe = "safe";
    public const string Suggestive = "suggestive";
    public const string Erotica = "erotica";
    public const string Pornographic = "pornographic";

    public static readonly string[] All = [Safe, Suggestive, Erotica, Pornographic];

    /// <summary>Unset falls back here — excludes only Pornographic, the previous hardcoded behavior.</summary>
    public const string Default = Erotica;

    public static bool IsValid(string? rating) => rating is not null && Array.IndexOf(All, rating) >= 0;

    /// <summary>The stored <see cref="SettingKeys.DiscoverMaxContentRating"/>, or <see cref="Default"/>
    /// if unset/invalid.</summary>
    public static async Task<string> GetMaxAsync(IAppSettings settings, CancellationToken ct)
    {
        var stored = await settings.GetAsync(SettingKeys.DiscoverMaxContentRating, ct);
        return IsValid(stored) ? stored! : Default;
    }

    /// <summary>Ratings at or below <paramref name="max"/> in <see cref="All"/>'s order.</summary>
    public static IReadOnlyList<string> Allowed(string max) =>
        All.Take(Array.IndexOf(All, max) + 1).ToList();
}
