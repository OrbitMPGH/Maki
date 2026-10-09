using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maki.Api.Json;

/// <summary>
/// The converters every wire format uses. MVC and the SignalR hub both apply this one list, so a DTO
/// reads the same whether it came from REST or arrived on <c>queueUpdated</c> and was spliced into
/// a cache the REST call filled.
/// </summary>
public static class MakiJson
{
    public static void ApplyConverters(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new UtcDateTimeConverter());
    }
}
