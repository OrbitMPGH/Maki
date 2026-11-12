using Maki.Api.Auth;
using Maki.Api.Services;
using Maki.Core.Configuration;
using Maki.Core.Sources;
using Maki.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Controllers;

/// <summary>
/// Preferred and blocked scanlation groups, globally and per series, for a source that lists several
/// groups' uploads of one chapter (<see cref="IScanlationGroupSource"/>, MangaDex today).
/// </summary>
[ApiController]
[Route("api/v1/scanlationgroups")]
public class ScanlationGroupsController(
    MakiDbContext db,
    IAppSettings settings,
    SourceRegistry sourceRegistry,
    SourceAvailability sourceAvailability,
    ILogger<ScanlationGroupsController> logger) : ControllerBase
{
    public record GroupLists(IReadOnlyList<string> Preferred, IReadOnlyList<string> Blocked);

    public record SeenGroup(string Name, int Chapters);

    /// <param name="Seen">Groups the library's chapters are credited to, most chapters first.</param>
    public record GlobalGroups(IReadOnlyList<string> Preferred, IReadOnlyList<string> Blocked, IReadOnlyList<SeenGroup> Seen);

    /// <param name="Global">What applies where this series' own lists say nothing.</param>
    /// <param name="Seen">Every group with an upload of this series on its sources, read live from them.</param>
    /// <param name="Sources">Display names of the series' sources that offer a choice of uploads.</param>
    /// <param name="Unreachable">Those of them whose listing failed just now, so their groups are missing from <paramref name="Seen"/>.</param>
    public record SeriesGroups(
        IReadOnlyList<string> Preferred, IReadOnlyList<string> Blocked, GroupLists Global,
        IReadOnlyList<SeenGroup> Seen, IReadOnlyList<string> Sources, IReadOnlyList<string> Unreachable);

    private const int MaxSeen = 200;

    [Authorize(Policy = Policies.Admin)]
    [HttpGet]
    public async Task<IActionResult> GetGlobal(CancellationToken ct)
    {
        var global = await ScanlationGroupSettings.GlobalAsync(settings, ct);
        var sourceNames = GroupSources().Select(s => s.Name).ToList();
        var credits = await db.ChapterSourceLinks.AsNoTracking()
            .Where(l => l.Group != null && sourceNames.Contains(l.SourceMapping!.SourceName))
            .GroupBy(l => l.Group!)
            .Select(g => new { Group = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var seen = credits
            .SelectMany(c => ScanlationGroupPreference.Members(c.Group).Select(name => (name, c.Count)))
            .GroupBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SeenGroup(g.First().name, g.Sum(x => x.Count)))
            .OrderByDescending(g => g.Chapters)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSeen)
            .ToList();
        return Ok(new GlobalGroups(global.Preferred, global.Blocked, seen));
    }

    [Authorize(Policy = Policies.Admin)]
    [HttpPut]
    public async Task<IActionResult> SetGlobal([FromBody] GroupLists request, CancellationToken ct)
    {
        var groups = ScanlationGroupPreference.Of(request.Preferred, request.Blocked);
        await ScanlationGroupSettings.SaveGlobalAsync(settings, groups, ct);
        return Ok(new GroupLists(groups.Preferred, groups.Blocked));
    }

    /// <summary>
    /// The series' lists and the groups its sources carry. Lists every upload from each such source,
    /// so it costs a request per source; the client asks only when the dialog opens.
    /// </summary>
    [Authorize(Policy = Policies.ManageSources)]
    [HttpGet("series/{id:int}")]
    public async Task<IActionResult> GetSeries(int id, CancellationToken ct)
    {
        var series = await db.Series.AsNoTracking()
            .Include(s => s.SourceMappings)
            .FirstOrDefaultAsync(s => s.Id == id, ct);
        if (series is null)
        {
            return NotFound();
        }

        var disabled = await sourceAvailability.DisabledAsync(ct);
        var uploads = new List<SourceChapter>();
        var sources = new List<string>();
        var unreachable = new List<string>();
        foreach (var mapping in series.SourceMappings.Where(m => m.Enabled && !disabled.Contains(m.SourceName)))
        {
            if (sourceRegistry.Find(mapping.SourceName) is not IScanlationGroupSource source)
            {
                continue;
            }

            sources.Add(source.DisplayName);
            try
            {
                uploads.AddRange(await source.ListUploadsAsync(mapping.SourceSeriesId, mapping.LanguageFilter, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogInformation(ex, "Could not list {Source} uploads of series {SeriesId}", source.Name, id);
                unreachable.Add(source.DisplayName);
            }
        }

        var global = await ScanlationGroupSettings.GlobalAsync(settings, ct);
        var own = ScanlationGroupSettings.OfSeries(series);
        var seen = ScanlationGroupChoice.Seen(uploads).Take(MaxSeen).Select(s => new SeenGroup(s.Name, s.Chapters)).ToList();
        return Ok(new SeriesGroups(own.Preferred, own.Blocked, new GroupLists(global.Preferred, global.Blocked),
            seen, sources.Distinct().ToList(), unreachable.Distinct().ToList()));
    }

    /// <summary>Saved for the next chapter refresh, which links the upload the lists now pick.</summary>
    [Authorize(Policy = Policies.ManageSources)]
    [HttpPut("series/{id:int}")]
    public async Task<IActionResult> SetSeries(int id, [FromBody] GroupLists request, CancellationToken ct)
    {
        var series = await db.Series.FindAsync([id], ct);
        if (series is null)
        {
            return NotFound();
        }

        var groups = ScanlationGroupPreference.Of(request.Preferred, request.Blocked);
        series.PreferredGroups = [.. groups.Preferred];
        series.BlockedGroups = [.. groups.Blocked];
        await db.SaveChangesAsync(ct);
        return Ok(new GroupLists(groups.Preferred, groups.Blocked));
    }

    private IEnumerable<ISource> GroupSources() => sourceRegistry.All.Where(s => s is IScanlationGroupSource);
}
