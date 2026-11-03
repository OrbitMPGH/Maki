using System.Globalization;

namespace Maki.Core.Sources.Custom;

/// <summary>The wire words a definition uses for <see cref="SourceKind"/>, <see cref="SourceContent"/>, <see cref="SourceRating"/> and a series' status.</summary>
public static class CustomSourceEnums
{
    public static SourceKind? Kind(string? value) => value?.ToLowerInvariant() switch
    {
        "official" => SourceKind.Official,
        "scanlator" => SourceKind.Scanlator,
        "aggregator" => SourceKind.Aggregator,
        _ => null
    };

    public static SourceRating? Rating(string? value) => value?.ToLowerInvariant() switch
    {
        "general" => SourceRating.General,
        "mature" => SourceRating.Mature,
        "adult" => SourceRating.Adult,
        _ => null
    };

    /// <summary>Null when the list is empty or names anything unknown.</summary>
    public static SourceContent? Content(IEnumerable<string>? values)
    {
        var content = SourceContent.None;
        foreach (var value in values ?? [])
        {
            content |= value.ToLowerInvariant() switch
            {
                "manga" => SourceContent.Manga,
                "manhwa" => SourceContent.Manhwa,
                "manhua" => SourceContent.Manhua,
                "webtoon" => SourceContent.Webtoon,
                "doujinshi" => SourceContent.Doujinshi,
                _ => (SourceContent)(-1)
            };
        }

        return content is SourceContent.None || (int)content < 0 ? null : content;
    }

    /// <summary>A series status word a site prints, in the spelling the rest of Maki stores; anything else is dropped.</summary>
    public static string? Status(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "ongoing" or "on going" or "publishing" or "releasing" => "Ongoing",
        "completed" or "complete" or "finished" or "end" or "ended" => "Completed",
        "hiatus" or "on hiatus" => "Hiatus",
        "cancelled" or "canceled" or "dropped" or "discontinued" => "Cancelled",
        _ => null
    };

    public static CultureInfo Culture(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return CultureInfo.InvariantCulture;
        }

        try
        {
            return CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }
}
