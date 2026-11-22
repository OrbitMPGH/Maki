using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maki.Core.Http.Proxy;

/// <summary>
/// The stored <c>sources.proxy</c> document: the proxies, and which source uses which. One document
/// so a save is one write. v1 writes a single proxy with id <see cref="DefaultId"/>; the list and the
/// id indirection are there so named proxies later need no migration.
/// </summary>
public sealed record SourceProxyConfig(
    IReadOnlyList<SourceProxyConfig.Entry> Proxies,
    IReadOnlyDictionary<string, string> Sources)
{
    public const string DefaultId = "default";

    public sealed record Entry(string Id, string Url);

    public static readonly SourceProxyConfig Empty = new([], new Dictionary<string, string>());

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The v1 proxy's URL, or null when none is set.</summary>
    public string? DefaultUrl => Proxies.FirstOrDefault(p => p.Id == DefaultId)?.Url;

    /// <summary>Unset or unreadable reads as <see cref="Empty"/>; a bad document never blocks startup.</summary>
    public static SourceProxyConfig Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Empty;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(raw, Json);
            return new SourceProxyConfig(
                stored?.Proxies?.Where(p => !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Url)).ToList() ?? [],
                new Dictionary<string, string>(stored?.Sources ?? [], StringComparer.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    /// <summary>Null for a document with nothing in it, so the setting row is deleted rather than stored empty.</summary>
    public string? Serialize() =>
        Proxies.Count == 0 && Sources.Count == 0
            ? null
            : JsonSerializer.Serialize(new Stored { Proxies = [.. Proxies], Sources = new Dictionary<string, string>(Sources) }, Json);

    private sealed class Stored
    {
        public List<Entry>? Proxies { get; set; }
        public Dictionary<string, string>? Sources { get; set; }
    }
}

/// <summary>Checks a proxy URL typed by the admin. v1 takes plain HTTP proxies without credentials.</summary>
public static class SourceProxyUrl
{
    /// <summary>The normalised <c>http://host:port</c>, or null with a localizer key saying why not.</summary>
    public static Uri? Parse(string? value, out string? errorKey)
    {
        errorKey = null;
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
        {
            errorKey = "error.settings.sourceProxyInvalidUrl";
            return null;
        }

        if (uri.Scheme != Uri.UriSchemeHttp)
        {
            errorKey = "error.settings.sourceProxyHttpOnly";
            return null;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            errorKey = "error.settings.sourceProxyNoCredentials";
            return null;
        }

        if (string.IsNullOrEmpty(uri.Host) || !HasExplicitPort(value.Trim()) || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            errorKey = "error.settings.sourceProxyInvalidUrl";
            return null;
        }

        return new Uri(uri.GetLeftPart(UriPartial.Authority));
    }

    // Uri fills in 80 for a bare host, but a proxy on port 80 is rare enough that a missing port is
    // far more likely a mistake.
    private static bool HasExplicitPort(string value)
    {
        var authority = value[(value.IndexOf("://", StringComparison.Ordinal) + 3)..];
        var slash = authority.IndexOf('/');
        if (slash >= 0)
        {
            authority = authority[..slash];
        }

        var colon = authority.LastIndexOf(':');
        return colon > authority.LastIndexOf(']') && colon < authority.Length - 1 && authority[(colon + 1)..].All(char.IsAsciiDigit);
    }
}
