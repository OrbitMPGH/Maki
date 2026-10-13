using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maki.Api.Json;

/// <summary>
/// <c>MakiDbContext</c> stamps Kind=Utc on every DateTime it reads, but a value built anywhere else
/// (a DTO filled from a parsed string, a file timestamp) can still be Kind=Unspecified. The default
/// serializer then omits the "Z" suffix, and browsers parse an offset-less ISO string as local
/// time, not UTC, silently shifting every timestamp in the UI by the viewer's UTC offset.
/// All DateTimes here are UTC in practice, so this converter stamps Kind=Utc before writing
/// the "Z" suffix, regardless of what Kind the value carries coming in. System.Text.Json applies it
/// to <c>DateTime?</c> as well.
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
