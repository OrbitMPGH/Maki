using System.Text.Json;
using Maki.Core.Sources;
using Microsoft.Extensions.Logging;

namespace Maki.Sources.Suwayomi;

/// <summary>
/// Supplies one <see cref="SuwayomiSource"/> per source stored in <c>suwayomi.sources</c>, every
/// installed one, built from that snapshot alone so the list does not depend on Suwayomi being
/// reachable. Nothing is supplied while no Suwayomi URL is set. Whether each is used is the ordinary
/// per-source switch, which starts off for all of them.
/// </summary>
public sealed class SuwayomiSourceProvider(
    SuwayomiSettings settings,
    SuwayomiClient client,
    SuwayomiMangaIdCache cache,
    ILogger<SuwayomiSourceProvider> logger) : ISourceProvider
{
    private volatile IReadOnlyList<ISource> _current = [];

    public IReadOnlyList<ISource> Current => _current;

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        var connection = await settings.GetAsync(ct);
        if (connection is null)
        {
            _current = [];
            return;
        }

        IReadOnlyList<SuwayomiStoredSource> stored;
        try
        {
            stored = await settings.StoredAsync(ct);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Ignoring the stored Suwayomi source list: it is not valid JSON");
            _current = [];
            return;
        }

        var sources = new List<ISource>();
        foreach (var entry in stored.DistinctBy(e => e.Id))
        {
            if (!entry.HasValidId)
            {
                logger.LogWarning("Skipping Suwayomi source {Id} ({Name}): not a source id", entry.Id, entry.Name);
                continue;
            }

            sources.Add(new SuwayomiSource(entry, connection.BaseUri, client, cache));
        }

        _current = sources;
    }
}
