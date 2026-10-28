using System.Text.RegularExpressions;
using Maki.Core.Http;
using Maki.Core.Sources;
using Maki.Core.Sources.Custom;
using Microsoft.Extensions.Logging;

namespace Maki.Sources.Custom;

/// <summary>Builds a <see cref="CustomSource"/> from a definition with the shared fetcher, page client and throttle.</summary>
public sealed class CustomSourceFactory(CustomSourceFetcher fetcher, IHttpClientFactory http, CustomSourceThrottle throttle)
{
    /// <summary>A source for the registry; its saved interval becomes the gate's.</summary>
    public CustomSource Create(CustomSourceDefinition definition) => Build(definition, throttle.For(definition.SourceName, definition.MinIntervalMs));

    /// <summary>A source for a draft, a test or a comparison, sharing the saved source's gate as it stands.</summary>
    public CustomSource CreateDraft(CustomSourceDefinition definition) => Build(definition, throttle.Shared(definition.SourceName, definition.MinIntervalMs));

    private CustomSource Build(CustomSourceDefinition definition, SpacingGate gate) =>
        new(definition, fetcher, http, gate, (host, referer) => fetcher.SessionHeadersFor(host, referer));
}

/// <summary>
/// Supplies one source per stored custom definition. A definition this build can't use still
/// registers, as an <see cref="InvalidCustomSource"/>, so its linked series fail visibly instead of
/// being skipped as mappings of a source that no longer exists.
/// </summary>
public sealed partial class CustomSourceProvider(
    CustomSourceStore store,
    CustomSourceFactory factory,
    ILogger<CustomSourceProvider> logger) : ISourceProvider
{
    private volatile IReadOnlyList<ISource> _current = [];

    public IReadOnlyList<ISource> Current => _current;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,39}$")]
    private static partial Regex IdPattern();

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        var sources = new List<ISource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in await store.ReadAsync(ct))
        {
            if (entry.Id is not { } id || !IdPattern().IsMatch(id) || !seen.Add(id))
            {
                logger.LogWarning("Skipping a stored custom source with a missing, invalid or repeated id ({Id})", entry.Id);
                continue;
            }

            if (entry.Definition is { } definition)
            {
                sources.Add(factory.Create(definition));
                continue;
            }

            logger.LogWarning("Custom source {Id} is invalid and registered as not working: {Problem}", id, entry.Problem);
            var name = entry.Raw["name"]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? entry.Raw["name"]!.GetValue<string>() : id;
            var baseUrl = entry.Raw["baseUrl"]?.GetValueKind() == System.Text.Json.JsonValueKind.String ? entry.Raw["baseUrl"]!.GetValue<string>() : "";
            sources.Add(new InvalidCustomSource(id, name, baseUrl, entry.Problem ?? "unreadable"));
        }

        _current = sources;
    }
}
