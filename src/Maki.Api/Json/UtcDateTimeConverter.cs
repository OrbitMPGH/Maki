using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maki.Api.Json;

/// <summary>
/// EF Core's SQLite provider round-trips DateTime as text with no offset, so every value comes
/// back Kind=Unspecified even though every write site uses DateTime.UtcNow. The default
/// serializer then omits the "Z" suffix, and browsers parse an offset-less ISO string as local
/// time, not UTC, silently shifting every timestamp in the UI by the viewer's UTC offset.
/// All stored DateTimes are UTC in practice, so this converter stamps Kind=Utc before writing
/// the "Z" suffix, regardless of what Kind the value carries coming in.
/// </summary>
public sealed class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetDateTime();
        return value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
        writer.WriteStringValue(utc);
    }
}

public sealed class UtcNullableDateTimeConverter : JsonConverter<DateTime?>
{
    private static readonly UtcDateTimeConverter Inner = new();

    public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        return Inner.Read(ref reader, typeToConvert, options);
    }

    public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }
        Inner.Write(writer, value.Value, options);
    }
}
