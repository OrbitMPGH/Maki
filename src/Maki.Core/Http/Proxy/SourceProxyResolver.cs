using Maki.Core.Configuration;

namespace Maki.Core.Http.Proxy;

/// <summary>Which <see cref="ProxyRoute"/> a source's traffic takes right now.</summary>
public interface ISourceProxyResolver
{
    /// <summary>
    /// The route for <paramref name="sourceName"/>: its assigned proxy, or <see cref="ProxyRoute.Direct"/>
    /// when it has none. Call once at the start of an operation and carry the result.
    /// </summary>
    /// <exception cref="SourceProxyException">The source is assigned a proxy that no longer exists.</exception>
    Task<ProxyRoute> ResolveAsync(string sourceName, CancellationToken ct = default);
}

/// <summary>
/// Reads <see cref="SettingKeys.SourceProxy"/> on every call. <see cref="IAppSettings"/> caches the raw
/// value and drops it when the key is written, so this only re-parses after a save.
/// </summary>
public sealed class SourceProxyResolver(IAppSettings settings) : ISourceProxyResolver
{
    private (string? Raw, SourceProxyConfig Config) _parsed = (null, SourceProxyConfig.Empty);

    public async Task<ProxyRoute> ResolveAsync(string sourceName, CancellationToken ct = default)
    {
        var config = await CurrentAsync(ct);
        if (!config.Sources.TryGetValue(sourceName, out var id))
        {
            return ProxyRoute.Direct;
        }

        var entry = config.Proxies.FirstOrDefault(p => p.Id == id);
        var url = entry is null ? null : SourceProxyUrl.Parse(entry.Url, out _);
        return url is null
            ? throw new SourceProxyException(sourceName, SourceProxyFailure.Misconfigured, null,
                $"{sourceName} is set to use a proxy that is not configured")
            : ProxyRoute.Via(url);
    }

    public async Task<SourceProxyConfig> CurrentAsync(CancellationToken ct = default)
    {
        var raw = await settings.GetAsync(SettingKeys.SourceProxy, ct);
        var parsed = _parsed;
        if (parsed.Raw == raw)
        {
            return parsed.Config;
        }

        var config = SourceProxyConfig.Parse(raw);
        _parsed = (raw, config);
        return config;
    }
}

/// <summary>A route that resolves to direct for every source, for tests and tools without settings.</summary>
public sealed class NoSourceProxies : ISourceProxyResolver
{
    public static readonly NoSourceProxies Instance = new();

    public Task<ProxyRoute> ResolveAsync(string sourceName, CancellationToken ct = default) => Task.FromResult(ProxyRoute.Direct);
}
