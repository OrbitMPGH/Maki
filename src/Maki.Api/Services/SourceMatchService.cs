using System.Text.RegularExpressions;
using Maki.Core.Entities;
using Maki.Core.Scrobbling;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Tries to link a freshly added series to site sources by title search.
/// Only creates a mapping automatically when a search result's title similarity
/// (see <see cref="ScrobbleMatching"/>) reaches <see cref="MatchThreshold"/>;
/// anything fuzzier is left for the user to pick in the UI.
/// </summary>
public partial class SourceMatchService(
    MakiDbContext db,
    SourceRegistry sourceRegistry,
    Maki.Core.Configuration.IAppSettings settings,
    ILogger<SourceMatchService> logger)
{
    /// <summary>
    /// Lower than <see cref="ScrobbleMatching.MatchThreshold"/>: source search results
    /// legitimately include subtitle variants ("Hajime no Ippo" vs "...: Fighting Spirit!"),
    /// which score well below the scrobbling threshold meant for zero-review auto-accept.
    /// </summary>
    private const double MatchThreshold = 0.6;

    [GeneratedRegex(@"[^a-z0-9]")]
    private static partial Regex NonAlphanumeric();

    public static string Normalize(string title) =>
        NonAlphanumeric().Replace(title.ToLowerInvariant(), string.Empty);

    /// <summary>
    /// series.OriginalTitle, unless it's just a generic franchise banner (a proper
    /// prefix of Title, e.g. "NARUTO" as the original title of "Naruto: The Seventh
    /// Hokage and the Scarlet Spring") - that's too generic to disambiguate and can
    /// exactly equal an unrelated sibling/parent series' title in search results.
    /// </summary>
    private static string? DisambiguatingOriginalTitle(Series series)
    {
        if (string.IsNullOrWhiteSpace(series.OriginalTitle))
        {
            return null;
        }

        var normalizedOriginal = Normalize(series.OriginalTitle);
        var normalizedTitle = Normalize(series.Title);
        var isGenericPrefix = normalizedOriginal.Length < normalizedTitle.Length
            && normalizedTitle.StartsWith(normalizedOriginal, StringComparison.Ordinal);

        return isGenericPrefix ? null : series.OriginalTitle;
    }

    /// <summary>
    /// Sources named in the "sources.priorityorder" CSV setting, in that order, followed by any
    /// remaining registered sources in registration order. Unknown names in the setting are ignored.
    /// </summary>
    public static List<ISource> OrderSources(IReadOnlyCollection<ISource> all, string? priorityCsv)
    {
        var preferred = (priorityCsv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => all.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
            .Where(s => s is not null)
            .Cast<ISource>()
            .ToList();

        return preferred.Concat(all.Where(s => !preferred.Contains(s))).ToList();
    }

    /// <returns>Names of sources that were automatically mapped.</returns>
    public async Task<List<string>> AutoMatchAsync(Series series, CancellationToken ct = default)
    {
        var mapped = new List<string>();

        var orderedSources = OrderSources(
            sourceRegistry.All, await settings.GetAsync(Maki.Core.Configuration.SettingKeys.SourcePriorityOrder, ct));

        foreach (var (source, priority) in orderedSources.Select((s, i) => (s, i + 1)))
        {
            if (await db.SourceMappings.AnyAsync(m => m.SeriesId == series.Id && m.SourceName == source.Name, ct))
            {
                continue;
            }

            try
            {
                var results = await source.SearchAsync(series.Title, ct);
                var candidates = results
                    .Select(r => new ScrobbleCandidate(r.SourceSeriesId, r.Title, [], r.Url))
                    .ToList();
                var best = ScrobbleMatching.BestCandidate(series.Title, DisambiguatingOriginalTitle(series), candidates, MatchThreshold);
                if (best is null)
                {
                    continue;
                }

                var match = results.First(r => r.SourceSeriesId == best.Id);

                db.SourceMappings.Add(new SourceMapping
                {
                    SeriesId = series.Id,
                    SourceName = source.Name,
                    SourceSeriesId = match.SourceSeriesId,
                    Url = match.Url,
                    Priority = priority,
                    Enabled = true
                });
                mapped.Add(source.Name);
                logger.LogInformation("Auto-matched {Title} to {Source} ({SourceId})",
                    series.Title, source.Name, match.SourceSeriesId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Source search failed on {Source} for {Title}", source.Name, series.Title);
            }
        }

        if (mapped.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return mapped;
    }
}
