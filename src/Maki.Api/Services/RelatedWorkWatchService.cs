using System.Text.Json;
using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Inbox;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

/// <summary>
/// Tells the readers of a library series when MangaBaka lists a new sequel, prequel, spin-off, side
/// story or main story for it.
///
/// <para>
/// The seen set (<see cref="SettingKeys.DiscoverRelatedSeen"/>) records, per library series'
/// MangaBaka id, every relation id the dump has listed for it. Only an id missing from a series'
/// set is new. A series not in the set yet (the first run on an instance, or a series added since
/// the last run) is recorded silently, so neither an upgrade nor an add announces the back catalogue.
/// </para>
///
/// <para>
/// The audience is the series' trackers (<see cref="InboxAudience.SeriesTrackers"/>, root-folder
/// visibility included), then each reader's content ceiling is tested against the new work. The row carries the library series' id, so per-series muting applies.
/// </para>
/// </summary>
public class RelatedWorkWatchService(
    IServiceScopeFactory scopeFactory,
    IAppSettings settings,
    MangaBakaLocalStore store,
    InboxAudienceResolver audiences,
    InboxService inbox,
    ILogger<RelatedWorkWatchService> logger)
{
    /// <summary>Up to this many new works become one notification each; more become a single summary.</summary>
    public const int MaxSingleNotifications = 3;

    public sealed record Candidate(int SeriesId, int RootFolderId, long RelatedId, string Kind);

    public async Task RunAsync(CancellationToken ct = default)
    {
        if (!await store.IsAvailableAsync(ct))
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MakiDbContext>();

        var library = await db.Series
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.MangaBakaId != null)
            .Select(s => new { s.Id, MangaBakaId = (long)s.MangaBakaId!.Value, s.RootFolderId })
            .ToListAsync(ct);
        var owned = library.Select(s => s.MangaBakaId).ToHashSet();

        var relations = await store.GetDirectRelationIdsAsync(owned, ct);
        var seen = ParseSeen(await settings.GetAsync(SettingKeys.DiscoverRelatedSeen, ct));

        var next = new Dictionary<long, HashSet<long>>();
        var candidates = new List<Candidate>();
        foreach (var series in library)
        {
            var current = relations.GetValueOrDefault(series.MangaBakaId) ?? [];
            if (next.ContainsKey(series.MangaBakaId))
            {
                continue;
            }

            if (seen is not null && seen.TryGetValue(series.MangaBakaId, out var known))
            {
                candidates.AddRange(current
                    .Where(r => !known.Contains(r.Id) && !owned.Contains(r.Id))
                    .Select(r => new Candidate(series.Id, series.RootFolderId, r.Id, r.Kind)));
                next[series.MangaBakaId] = [.. known, .. current.Select(r => r.Id)];
            }
            else
            {
                next[series.MangaBakaId] = current.Select(r => r.Id).ToHashSet();
            }
        }

        if (seen is null)
        {
            await SaveSeenAsync(next, ct);
            logger.LogInformation("Related work check seeded for {Count} library series", next.Count);
            return;
        }

        if (candidates.Count > 0)
        {
            // An id the dump now lists under its merge target is a work we already knew by another id.
            var canonical = await store.ResolveCanonicalIdsAsync(candidates.Select(c => c.RelatedId).ToList(), ct);
            var byMangaBaka = library.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().MangaBakaId);
            var fresh = new List<Candidate>();
            foreach (var c in candidates)
            {
                var id = canonical.GetValueOrDefault(c.RelatedId, c.RelatedId);
                var seed = byMangaBaka[c.SeriesId];
                if (owned.Contains(id) || (id != c.RelatedId && seen[seed].Contains(id)))
                {
                    continue;
                }

                next[seed].Add(id);
                fresh.Add(c with { RelatedId = id });
            }

            await NotifyAsync(db, fresh, ct);
        }

        await SaveSeenAsync(next, ct);
        if (candidates.Count > 0)
        {
            logger.LogInformation("Related work check found {Count} new related work(s)", candidates.Count);
        }
    }

    private async Task NotifyAsync(MakiDbContext db, IReadOnlyList<Candidate> candidates, CancellationToken ct)
    {
        if (candidates.Count == 0)
        {
            return;
        }

        var perUser = new Dictionary<int, List<Candidate>>();
        foreach (var group in candidates.GroupBy(c => (c.SeriesId, c.RootFolderId)))
        {
            var users = await audiences.ResolveAsync(
                InboxAudience.SeriesTrackers(group.Key.SeriesId, group.Key.RootFolderId), ct);
            foreach (var userId in users)
            {
                if (!perUser.TryGetValue(userId, out var list))
                {
                    perUser[userId] = list = [];
                }

                list.AddRange(group);
            }
        }

        if (perUser.Count == 0)
        {
            return;
        }

        var userIds = perUser.Keys.ToList();
        var ceilings = await db.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.MaxContentRating, ct);
        var relatedIds = candidates.Select(c => c.RelatedId).Distinct().ToList();
        var known = await KnownAsync(db, userIds, relatedIds, ct);

        var byCeiling = new Dictionary<string, IReadOnlyDictionary<long, MangaBakaShortlistRow>>(StringComparer.Ordinal);
        foreach (var (userId, list) in perUser)
        {
            var allowed = ContentRating.Allowed(ceilings.GetValueOrDefault(userId));
            if (!byCeiling.TryGetValue(allowed[^1], out var rows))
            {
                rows = await store.GetShortlistRowsAsync(relatedIds, allowed, ct);
                byCeiling[allowed[^1]] = rows;
            }

            var wanted = list
                .Where(c => !known.Contains((userId, c.RelatedId)))
                .Select(c => (Candidate: c, Item: rows.GetValueOrDefault(c.RelatedId)?.Item))
                .Where(x => x.Item is not null)
                .DistinctBy(x => x.Candidate.RelatedId)
                .Select(x => (x.Candidate, x.Item!))
                .ToList();

            await TryNotifyAsync(userId, wanted, ct);
        }
    }

    /// <summary>Works a reader has hidden or shortlisted, which they plainly already know about.</summary>
    private static async Task<HashSet<(int UserId, long MangaBakaId)>> KnownAsync(
        MakiDbContext db, IReadOnlyList<int> userIds, IReadOnlyList<long> ids, CancellationToken ct)
    {
        var hidden = await db.RecommendationFeedback
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => userIds.Contains(x.UserId) && ids.Contains(x.ProviderId) &&
                        x.Suppression == RecommendationSuppression.Hidden)
            .Select(x => new { x.UserId, x.ProviderId })
            .ToListAsync(ct);
        var known = hidden.Select(x => (x.UserId, x.ProviderId)).ToHashSet();

        var keys = ids.Where(id => id is > 0 and <= int.MaxValue).Select(id => (int)id).ToList();
        var shortlisted = await db.ShortlistEntries
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(x => userIds.Contains(x.UserId) && keys.Contains(x.MangaBakaId))
            .Select(x => new { x.UserId, x.MangaBakaId })
            .ToListAsync(ct);
        known.UnionWith(shortlisted.Select(x => (x.UserId, (long)x.MangaBakaId)));
        return known;
    }

    /// <summary>One retry, then the reader is skipped; the seen set moves regardless, as the follow check's watermark does.</summary>
    private async Task TryNotifyAsync(
        int userId, IReadOnlyList<(Candidate Candidate, MangaBakaRecommendation Item)> works, CancellationToken ct)
    {
        if (works.Count == 0)
        {
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await NotifyUserAsync(userId, works, ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not save related work notifications for user {UserId} (attempt {Attempt})", userId, attempt);
                if (attempt == 2)
                {
                    return;
                }
            }
        }
    }

    private async Task NotifyUserAsync(
        int userId, IReadOnlyList<(Candidate Candidate, MangaBakaRecommendation Item)> works, CancellationToken ct)
    {
        if (works.Count > MaxSingleNotifications)
        {
            await inbox.RaiseOrThrowAsync(InboxEventType.NewRelatedWork, new InboxMessage(
                "inbox.relatedWorks",
                new Dictionary<string, object?> { ["count"] = works.Count },
                Url: "/discover"), InboxAudience.User(userId), ct);
            return;
        }

        foreach (var (candidate, item) in works)
        {
            await inbox.RaiseOrThrowAsync(InboxEventType.NewRelatedWork, new InboxMessage(
                "inbox.relatedWork",
                // A catalogue title, like the follow check's: the work is in nobody's library yet.
                new Dictionary<string, object?> { ["kind"] = candidate.Kind, ["title"] = item.Title },
                SeriesId: candidate.SeriesId,
                Url: $"/discover?open={item.ProviderId}"), InboxAudience.User(userId), ct);
        }
    }

    internal static Dictionary<long, HashSet<long>>? ParseSeen(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<long, long[]>>(json)?
                .ToDictionary(x => x.Key, x => x.Value.ToHashSet());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Task SaveSeenAsync(Dictionary<long, HashSet<long>> seen, CancellationToken ct) =>
        settings.SetAsync(SettingKeys.DiscoverRelatedSeen,
            JsonSerializer.Serialize(seen.ToDictionary(x => x.Key, x => x.Value.Order().ToArray())), ct);
}
