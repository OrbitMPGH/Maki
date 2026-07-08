using System.Text.Json.Serialization;

namespace Mangarr.Sources.MangaDex;

internal class MdCollectionResponse<T>
{
    [JsonPropertyName("data")]
    public List<T> Data { get; set; } = [];

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }
}

internal class MdEntityResponse<T>
{
    [JsonPropertyName("data")]
    public T? Data { get; set; }
}

internal class MdManga
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("attributes")]
    public MdMangaAttributes Attributes { get; set; } = new();

    [JsonPropertyName("relationships")]
    public List<MdRelationship> Relationships { get; set; } = [];
}

internal class MdMangaAttributes
{
    [JsonPropertyName("title")]
    public Dictionary<string, string> Title { get; set; } = [];

    [JsonPropertyName("altTitles")]
    public List<Dictionary<string, string>> AltTitles { get; set; } = [];

    [JsonPropertyName("description")]
    public Dictionary<string, string> Description { get; set; } = [];

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

internal class MdRelationship
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("attributes")]
    public MdRelationshipAttributes? Attributes { get; set; }
}

internal class MdRelationshipAttributes
{
    [JsonPropertyName("fileName")]
    public string? FileName { get; set; }
}

internal class MdChapter
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("attributes")]
    public MdChapterAttributes Attributes { get; set; } = new();
}

internal class MdChapterAttributes
{
    [JsonPropertyName("chapter")]
    public string? Chapter { get; set; }

    [JsonPropertyName("volume")]
    public string? Volume { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("translatedLanguage")]
    public string? TranslatedLanguage { get; set; }

    [JsonPropertyName("externalUrl")]
    public string? ExternalUrl { get; set; }

    [JsonPropertyName("isUnavailable")]
    public bool IsUnavailable { get; set; }

    [JsonPropertyName("pages")]
    public int Pages { get; set; }

    [JsonPropertyName("publishAt")]
    public DateTime? PublishAt { get; set; }
}

internal class MdAtHomeResponse
{
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = string.Empty;

    [JsonPropertyName("chapter")]
    public MdAtHomeChapter Chapter { get; set; } = new();
}

internal class MdAtHomeChapter
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public List<string> Data { get; set; } = [];
}
