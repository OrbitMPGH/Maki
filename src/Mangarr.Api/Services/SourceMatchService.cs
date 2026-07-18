using System.Text.RegularExpressions;
using Mangarr.Core.Entities;
using Mangarr.Core.Sources;
using Mangarr.Data;
using Microsoft.EntityFrameworkCore;

namespace Mangarr.Api.Services;

/// <summary>
/// Tries to link a freshly added series to site sources by title search.
/// Only creates a mapping automatically when a normalized title matches exactly;
/// anything fuzzier is left for the user to pick in the UI.
/// </summary>
public partial class SourceMatchService(
    MangarrDbContext db,
    SourceRegistry sourceRegistry,
    Mangarr.Core.Configuration.IAppSettings settings,
    ILogger<SourceMatchService> logger)
{
    [GeneratedRegex(@"[^a-z0-9]")]
    private static partial Regex NonAlphanumeric();

    public static string Normalize(string title) =>
        NonAlphanumeric().Replace(title.ToLowerInvariant(), string.Empty);

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
        var titles = new List<string> { series.Title };
        if (!string.IsNullOrWhiteSpace(series.OriginalTitle))
        {
            titles.Add(series.OriginalTitle);
        }

        var normalizedTitles = titles.Select(Normalize).ToHashSet();

        var orderedSources = OrderSources(
            sourceRegistry.All, await settings.GetAsync(Mangarr.Core.Configuration.SettingKeys.SourcePriorityOrder, ct));

        foreach (var (source, priority) in orderedSources.Select((s, i) => (s, i + 1)))
        {
            if (await db.SourceMappings.AnyAsync(m => m.SeriesId == series.Id && m.SourceName == source.Name, ct))
            {
                continue;
            }

            try
            {
                var results = await source.SearchAsync(series.Title, ct);
                var match = results.FirstOrDefault(r => normalizedTitles.Contains(Normalize(r.Title)))
                    // Subtitle variants ("Hajime no Ippo" vs "Hajime no Ippo: Fighting Spirit!"):
                    // accept when one normalized title is a prefix of the other.
                    ?? results.FirstOrDefault(r =>
                    {
                        var candidate = Normalize(r.Title);
                        return candidate.Length >= 6 && normalizedTitles.Any(t =>
                            t.Length >= 6 && (t.StartsWith(candidate) || candidate.StartsWith(t)));
                    });
                if (match is null)
                {
                    continue;
                }

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
