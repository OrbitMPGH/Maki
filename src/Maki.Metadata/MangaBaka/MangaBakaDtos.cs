using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maki.Metadata.MangaBaka;

// Response shapes for api.mangabaka.org v1. The upstream schema is explicitly
// unstable, so only the fields Maki consumes are modeled.

internal class MangaBakaSearchResponse
{
    [JsonPropertyName("data")]
    public List<MangaBakaSeries> Data { get; set; } = [];
}

internal class MangaBakaGetResponse
{
    [JsonPropertyName("data")]
    public MangaBakaSeries? Data { get; set; }
}

internal class MangaBakaSeries
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("merged_with")]
    [JsonConverter(typeof(LenientCountConverter))]
    public int? MergedWith { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("native_title")]
    public string? NativeTitle { get; set; }

    [JsonPropertyName("romanized_title")]
    public string? RomanizedTitle { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("content_rating")]
    public string? ContentRating { get; set; }

    [JsonPropertyName("final_volume")]
    [JsonConverter(typeof(LenientCountConverter))]
    public int? FinalVolume { get; set; }

    [JsonPropertyName("total_chapters")]
    [JsonConverter(typeof(LenientCountConverter))]
    public int? TotalChapters { get; set; }

    [JsonPropertyName("authors")]
    public List<string> Authors { get; set; } = [];

    [JsonPropertyName("artists")]
    public List<string> Artists { get; set; } = [];

    /// <summary>Objects (<c>{"name","note","type"}</c>) or occasionally bare strings; parsed by <see cref="MangaBakaProvider"/>.</summary>
    [JsonPropertyName("publishers")]
    public JsonElement Publishers { get; set; }

    [JsonPropertyName("genres")]
    public List<string> Genres { get; set; } = [];

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("cover")]
    public MangaBakaCover? Cover { get; set; }

    [JsonPropertyName("source")]
    public MangaBakaSources? Source { get; set; }
}

internal class MangaBakaCover
{
    [JsonPropertyName("raw")]
    public MangaBakaCoverVariant? Raw { get; set; }
}

internal class MangaBakaCoverVariant
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

internal class MangaBakaSources
{
    [JsonPropertyName("anilist")]
    public MangaBakaSourceRef? AniList { get; set; }

    [JsonPropertyName("my_anime_list")]
    public MangaBakaSourceRef? MyAnimeList { get; set; }

    [JsonPropertyName("manga_updates")]
    public MangaBakaSourceRefString? MangaUpdates { get; set; }

    [JsonPropertyName("kitsu")]
    public MangaBakaSourceRef? Kitsu { get; set; }
}

internal class MangaBakaSourceRef
{
    [JsonPropertyName("id")]
    public int? Id { get; set; }
}

internal class MangaBakaSourceRefString
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

/// <summary>
/// MangaBaka stores these counts as TEXT and sends them as strings, sometimes fractional ("112.5"),
/// sometimes as numbers. Anything unreadable is null rather than a failed deserialization, which
/// would lose the whole response over one field.
/// </summary>
internal sealed class LenientCountConverter : JsonConverter<int?>
{
    public override bool HandleNull => true;

    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var whole) ? whole : Truncate(reader.GetDouble());
            case JsonTokenType.String:
                var text = reader.GetString();
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }

                return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fractional)
                    ? Truncate(fractional)
                    : null;
            case JsonTokenType.StartObject or JsonTokenType.StartArray:
                reader.Skip();
                return null;
            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is { } v)
        {
            writer.WriteNumberValue(v);
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    private static int? Truncate(double value) =>
        double.IsFinite(value) && value is >= int.MinValue and <= int.MaxValue ? (int)value : null;
}
