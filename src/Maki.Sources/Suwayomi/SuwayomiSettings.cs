using System.Text.Json;
using System.Text.Json.Serialization;
using Maki.Core.Configuration;

namespace Maki.Sources.Suwayomi;

public enum SuwayomiAuthMode
{
    None,
    Basic,
    UiLogin
}

/// <summary>Where Suwayomi is and how to sign in, as one comparable value.</summary>
/// <param name="BaseUri">Includes any <c>server.webUISubpath</c>, without a trailing slash.</param>
public sealed record SuwayomiConnection(Uri BaseUri, SuwayomiAuthMode AuthMode, string? Username, string? Password)
{
    public Uri GraphQlUri => Resolve("/api/graphql");

    /// <summary>
    /// Resolves a path Suwayomi handed back (<c>/api/v1/manga/7/chapter/1/page/0</c>) against the
    /// base. Those paths are hardcoded without the subpath, while the API is served under it, so
    /// they are appended to the base path rather than resolved like an ordinary relative URL.
    /// </summary>
    public Uri Resolve(string path)
    {
        if (!path.StartsWith('/'))
        {
            throw new InvalidOperationException($"Suwayomi returned '{path}' where a server-relative path was expected");
        }

        return new Uri(BaseUri.GetLeftPart(UriPartial.Authority) + BaseUri.AbsolutePath.TrimEnd('/') + path);
    }

    public bool IsSameOrigin(Uri uri) =>
        uri.IsAbsoluteUri &&
        string.Equals(uri.Scheme, BaseUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.IdnHost, BaseUri.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        uri.Port == BaseUri.Port;

    // The password stays out of logs and exception messages.
    public override string ToString() => $"{BaseUri} ({AuthMode})";
}

/// <summary>
/// One source installed in Suwayomi, as stored in <see cref="SettingKeys.SuwayomiSources"/>. Every
/// installed source is stored and registered; whether Maki uses it is the ordinary per-source switch.
/// </summary>
/// <param name="Id">The Mihon source id, a non-negative long kept as a string as Suwayomi sends it.</param>
/// <param name="Lang">The source's own language code; <c>all</c>/<c>multi</c> for sources that mix languages.</param>
/// <param name="ChapterLanguage">
/// The language its chapters are filed under. Required for a mixed-language source, where Suwayomi
/// cannot say; defaults to <paramref name="Lang"/> otherwise.
/// </param>
/// <param name="ContentWarning"><c>SAFE</c>, <c>MIXED</c> or <c>NSFW</c>; null for a v2.1 server, which reports only <paramref name="IsNsfw"/>.</param>
/// <param name="Missing">Suwayomi no longer listed it at the last refresh. Kept only while series are linked to it.</param>
public sealed record SuwayomiStoredSource(
    string Id,
    string Name,
    string DisplayName,
    string Lang,
    string? ChapterLanguage = null,
    string? ContentWarning = null,
    bool IsNsfw = false,
    bool Missing = false)
{
    /// <summary>Mihon's codes for a source that publishes several languages under one id.</summary>
    public static bool IsMixedLanguage(string lang) =>
        lang.Equals("all", StringComparison.OrdinalIgnoreCase) ||
        lang.Equals("multi", StringComparison.OrdinalIgnoreCase) ||
        lang.Equals("other", StringComparison.OrdinalIgnoreCase);

    /// <summary>Null when the source cannot be used yet: a mixed-language source nobody picked a language for.</summary>
    [JsonIgnore]
    public string? EffectiveChapterLanguage =>
        !string.IsNullOrWhiteSpace(ChapterLanguage) ? ChapterLanguage.Trim()
        : IsMixedLanguage(Lang) || string.IsNullOrWhiteSpace(Lang) ? null
        : Lang.Trim();

    [JsonIgnore]
    public bool HasValidId => Id.Length is > 0 and <= 20 && Id.All(char.IsAsciiDigit);
}

/// <summary>The one reader of the <c>suwayomi.*</c> settings.</summary>
public sealed class SuwayomiSettings(IAppSettings settings)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Null when no usable URL is set, which is what "the integration is off" means.</summary>
    public async Task<SuwayomiConnection?> GetAsync(CancellationToken ct = default)
    {
        var url = await settings.GetAsync(SettingKeys.SuwayomiUrl, ct);
        if (!TryParseBaseUri(url, out var baseUri))
        {
            return null;
        }

        return new SuwayomiConnection(
            baseUri,
            ParseAuthMode(await settings.GetAsync(SettingKeys.SuwayomiAuthMode, ct)),
            await settings.GetAsync(SettingKeys.SuwayomiUsername, ct),
            await settings.GetAsync(SettingKeys.SuwayomiPassword, ct));
    }

    /// <summary>The stored snapshot as written. Throws <see cref="JsonException"/> on a malformed value.</summary>
    public async Task<IReadOnlyList<SuwayomiStoredSource>> StoredAsync(CancellationToken ct = default)
    {
        var raw = await settings.GetAsync(SettingKeys.SuwayomiSources, ct);
        return string.IsNullOrWhiteSpace(raw)
            ? []
            : JsonSerializer.Deserialize<List<SuwayomiStoredSource>>(raw, Json) ?? [];
    }

    public static string Serialize(IEnumerable<SuwayomiStoredSource> sources) =>
        JsonSerializer.Serialize(sources, Json);

    public static bool TryParseBaseUri(string? url, out Uri baseUri)
    {
        baseUri = null!;
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment) ||
            !string.IsNullOrEmpty(parsed.UserInfo))
        {
            return false;
        }

        baseUri = new Uri(parsed.GetLeftPart(UriPartial.Authority) + parsed.AbsolutePath.TrimEnd('/'));
        return true;
    }

    public static SuwayomiAuthMode ParseAuthMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "basic" => SuwayomiAuthMode.Basic,
        "uilogin" => SuwayomiAuthMode.UiLogin,
        _ => SuwayomiAuthMode.None
    };
}
