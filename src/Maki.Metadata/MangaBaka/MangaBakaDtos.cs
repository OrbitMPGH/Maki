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
    public int? FinalVolume { get; set; }

    [JsonPropertyName("total_chapters")]
    public int? TotalChapters { get; set; }

    [JsonPropertyName("authors")]
    public List<string> Authors { get; set; } = [];

    [JsonPropertyName("artists")]
    public List<string> Artists { get; set; } = [];

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
