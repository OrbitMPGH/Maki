using System.Text.Json;
using Maki.Core.Sources;
using Microsoft.Extensions.Logging;

namespace Maki.Sources.Suwayomi;

/// <summary>
/// Supplies one <see cref="SuwayomiSource"/> per entry the admin exposed in
/// <c>suwayomi.sources</c>, built from that snapshot alone so the list does not depend on Suwayomi
/// being reachable. Nothing is supplied while no Suwayomi URL is set.
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

        IReadOnlyList<SuwayomiExposedSource> exposed;
        try
        {
            exposed = await settings.ExposedAsync(ct);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Ignoring the stored Suwayomi source list: it is not valid JSON");
            _current = [];
            return;
        }

        var sources = new List<ISource>();
        foreach (var entry in exposed.DistinctBy(e => e.Id))
        {
            if (!entry.HasValidId)
            {
                logger.LogWarning("Skipping Suwayomi source {Id} ({Name}): not a source id", entry.Id, entry.Name);
                continue;
            }

            // Filing a mixed-language source's chapters under a guessed language would put them in
            // the wrong series folder and write the wrong LanguageISO, so it waits for a choice.
            if (entry.EffectiveChapterLanguage is not { } language)
            {
                logger.LogWarning(
                    "Skipping Suwayomi source {Id} ({Name}): it mixes languages and has no chapter language set",
                    entry.Id, entry.Name);
                continue;
            }

            sources.Add(new SuwayomiSource(entry, language, connection.BaseUri, client, cache));
        }

        _current = sources;
    }
}
