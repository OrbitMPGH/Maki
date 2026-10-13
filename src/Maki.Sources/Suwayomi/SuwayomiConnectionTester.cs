using System.Globalization;
using System.Text.RegularExpressions;
using Maki.Core.Configuration;

namespace Maki.Sources.Suwayomi;

public sealed record SuwayomiTestResult(SuwayomiAbout About, int SourceCount);

/// <summary>The server is reachable but older than Maki supports for the chosen sign-in mode.</summary>
public sealed class SuwayomiVersionException(string version, string minimum)
    : Exception($"Suwayomi {version} is older than {minimum}")
{
    public string Version { get; } = version;
    public string Minimum { get; } = minimum;
}

public static partial class SuwayomiVersion
{
    /// <summary>
    /// The minimum for a sign-in mode, or null when <paramref name="version"/> meets it. A version
    /// string that does not parse is let through: a preview build's naming is not a reason to refuse it.
    /// </summary>
    public static string? Unmet(string version, SuwayomiAuthMode mode)
    {
        var (major, minor, label) = mode == SuwayomiAuthMode.UiLogin ? (2, 2, "v2.2") : (2, 1, "v2.1");
        var match = Pattern().Match(version);
        if (!match.Success)
        {
            return null;
        }

        var actual = (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
        return actual.CompareTo((major, minor)) < 0 ? label : null;
    }

    [GeneratedRegex(@"^\s*v?(\d+)\.(\d+)")]
    private static partial Regex Pattern();
}

/// <summary>
/// Checks a connection before it is saved: version, sign-in, and that the source list can be read.
/// Runs on its own handler chain built for the connection under test (the same origin and auth
/// handlers the real clients use) rather than on the named clients, which read the stored settings.
/// </summary>
public class SuwayomiConnectionTester(Func<HttpMessageHandler>? primary = null)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Throws <see cref="SuwayomiVersionException"/> for a server too old for the sign-in mode,
    /// and whatever the call threw otherwise (an <see cref="HttpRequestException"/> with 401 for
    /// refused credentials).
    /// </summary>
    public async Task<SuwayomiTestResult> TestAsync(SuwayomiConnection connection, CancellationToken ct = default)
    {
        using var handler = primary?.Invoke() ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };
        var settings = new SuwayomiSettings(new FixedSettings(connection));
        var factory = new ChainFactory(settings, handler);
        var tokens = new SuwayomiTokenStore(settings, factory);
        factory.Tokens = tokens;
        var client = new SuwayomiClient(factory, settings, TimeProvider.System);

        // Version first: before v2.2 there is no login mutation, and a sign-in failure would hide the reason.
        var about = await client.AboutAsync(ct, anonymous: connection.AuthMode == SuwayomiAuthMode.UiLogin);
        if (SuwayomiVersion.Unmet(about.Version, connection.AuthMode) is { } minimum)
        {
            throw new SuwayomiVersionException(about.Version, minimum);
        }

        if (connection.AuthMode == SuwayomiAuthMode.UiLogin)
        {
            await tokens.RenewAsync(null, ct);
        }

        var sources = await client.ListSourcesAsync(ct);
        return new SuwayomiTestResult(about, sources.Count);
    }

    private sealed class FixedSettings(SuwayomiConnection connection) : IAppSettings
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(key switch
        {
            SettingKeys.SuwayomiUrl => connection.BaseUri.ToString(),
            SettingKeys.SuwayomiAuthMode => connection.AuthMode switch
            {
                SuwayomiAuthMode.Basic => "basic",
                SuwayomiAuthMode.UiLogin => "uilogin",
                _ => "none"
            },
            SettingKeys.SuwayomiUsername => connection.Username,
            SettingKeys.SuwayomiPassword => connection.Password,
            _ => null
        });

        public Task SetAsync(string key, string? value, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class ChainFactory(SuwayomiSettings settings, HttpMessageHandler primary) : IHttpClientFactory
    {
        public SuwayomiTokenStore? Tokens { get; set; }

        public HttpClient CreateClient(string name) =>
            new(new SuwayomiOriginHandler(settings)
            {
                InnerHandler = new SuwayomiAuthHandler(Tokens!) { InnerHandler = primary }
            }, disposeHandler: false)
            {
                Timeout = RequestTimeout
            };
    }
}
