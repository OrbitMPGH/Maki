using Maki.Core.Entities;
using Maki.Core.Tags;
using Maki.Data;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <param name="RuleId">The rule that picked the folder, or null for the fallback.</param>
public sealed record RootFolderRoute(int RootFolderId, int? RuleId);

/// <summary>
/// Picks the root folder for a series added without one: the first enabled rule, in position
/// order, whose conditions the series meets and whose folder the adder may use. Nothing matching
/// falls back to the first usable folder by id, which is what every add path picked before rules.
/// Only <c>SeriesCreationService</c> and the add form's preview call it; a library import's files
/// are already on disk, and an existing series is never moved.
/// </summary>
public class RootFolderRoutingService(MakiDbContext db)
{
    /// <param name="usable">The folders the adder may use, or null for every folder.</param>
    public async Task<RootFolderRoute?> RouteAsync(
        AutoTagSubject subject, IReadOnlyCollection<int>? usable, CancellationToken ct)
    {
        var folders = await db.RootFolders.AsNoTracking().OrderBy(f => f.Id).Select(f => f.Id).ToListAsync(ct);
        if (usable is not null)
        {
            folders = folders.Where(usable.Contains).ToList();
        }

        if (folders.Count == 0)
        {
            return null;
        }

        return await MatchAsync(subject, folders, ct) ?? new RootFolderRoute(folders[0], null);
    }

    /// <summary>The first enabled rule the subject meets among <paramref name="folders"/>, or null.</summary>
    public async Task<RootFolderRoute?> MatchAsync(
        AutoTagSubject subject, IReadOnlyCollection<int>? folders, CancellationToken ct)
    {
        var rules = await db.RootFolderRules.AsNoTracking()
            .Where(r => r.Enabled)
            .OrderBy(r => r.Position).ThenBy(r => r.Id)
            .ToListAsync(ct);
        var rule = rules.FirstOrDefault(r =>
            (folders is null || folders.Contains(r.RootFolderId)) &&
            ConditionGroups.Matches(ConditionGroups.Parse(r.ConditionsJson), subject));
        return rule is null ? null : new RootFolderRoute(rule.RootFolderId, rule.Id);
    }

    /// <summary>The folders a user may add into, or null when they may use every one.</summary>
    public async Task<IReadOnlyCollection<int>?> UsableByAsync(int userId, bool allRootFolders, CancellationToken ct) =>
        allRootFolders
            ? null
            : await db.UserRootFolders.AsNoTracking()
                .Where(g => g.UserId == userId)
                .Select(g => g.RootFolderId)
                .ToListAsync(ct);

    /// <summary>The subject a rule tests a series that is being added.</summary>
    public static AutoTagSubject SubjectFor(Series series, string? addedFrom, int? addedBy) =>
        AutoTagSubject.For(series, AutoTagAddedVia.FromAddedFrom(addedFrom), addedBy is > 0 ? addedBy : null);
}
