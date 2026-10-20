using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maki.Api.Dtos;

/// <summary>A kosync <c>PUT syncs/progress</c> body. Names are KOReader's, not Maki's camelCase.</summary>
public sealed class KoreaderProgressRequest
{
    [JsonPropertyName("document")]
    public string? Document { get; set; }

    /// <summary>A page number for comics and PDFs, an XPointer for reflowable books. Sent as a string or a number.</summary>
    [JsonPropertyName("progress")]
    public JsonElement Progress { get; set; }

    [JsonPropertyName("percentage")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? Percentage { get; set; }

    [JsonPropertyName("device")]
    public string? Device { get; set; }

    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }
}

public sealed class KoreaderRegisterRequest
{
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }
}

public sealed record KoreaderProgressResponse(
    [property: JsonPropertyName("document")] string Document,
    [property: JsonPropertyName("progress")] string Progress,
    [property: JsonPropertyName("percentage")] double Percentage,
    [property: JsonPropertyName("device")] string Device,
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("timestamp")] long Timestamp);

public sealed record KoreaderPushResponse(
    [property: JsonPropertyName("document")] string Document,
    [property: JsonPropertyName("timestamp")] long Timestamp);

public sealed record KoreaderError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message);
