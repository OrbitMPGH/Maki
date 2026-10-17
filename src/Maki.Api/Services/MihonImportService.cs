using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Maki.Api.Localization;
using Maki.Core.Metadata;
using Maki.Core.Mihon;
using Maki.Core.Scrobbling;
using Maki.Core.Security;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public record MihonParsedManga(
    int Index,
    string Title,
    string? Author,
    IReadOnlyList<string> Categories,
    int? AniListId,
    int? MalId,
    int? KitsuId,
    string? MangaBakaId,
    IReadOnlyList<decimal> ReadNumbers,
    decimal? TrackerLastRead,
    int ChapterCount,
    int ReadCount)
{
    public static MihonParsedManga From(MihonManga m) => new(
        m.Index, m.Title, m.Author, m.Categories, m.AniListId, m.MalId, m.KitsuId, m.MangaBakaId,
        m.ReadNumbers, m.TrackerLastRead, m.ChapterCount, m.ReadCount);
}

public record MihonParseResponse(IReadOnlyList<MihonParsedManga> Manga, int SkippedNonLibrary, int CategoryCount);

public record MihonResolveItem(
    int Index, string Title, string? Author, int? AniListId, int? MalId, int? KitsuId, string? MangaBakaId);

public record MihonResolveRequest(List<MihonResolveItem>? Items);

/// <param name="Status"><c>matched</c>, <c>needsReview</c> or <c>notFound</c>.</param>
/// <param name="MatchedBy"><c>mangabaka</c>, <c>anilist</c>, <c>mal</c>, <c>kitsu</c> or <c>title</c>; null unless matched.</param>
/// <param name="Library"><c>none</c>, <c>visible</c> or <c>elsewhere</c>, for <paramref name="Match"/> only.</param>
public record MihonResolveResult(
    int Index,
    string Status,
    string? MatchedBy,
    MetadataSearchResult? Match,
    IReadOnlyList<MetadataSearchResult> Candidates,
    string Library,
    int? SeriesId);

public record MihonResolveResponse(IReadOnlyList<MihonResolveResult> Results);

public record MihonImportItem(
    string ProviderId, List<string>? Categories, List<decimal>? ReadNumbers, decimal? TrackerLastRead);

/// <param name="MonitorNewItems">A <c>NewChapterMonitorMode</c> name, or null for the default mode, as on the add form.</param>
public record MihonImportRequest(
    string RunId,
    int RootFolderId,
    bool Monitored,
    string? MonitorNewItems,
    bool ApplyTags,
    bool ApplyReadState,
    List<MihonImportItem>? Items);

/// <param name="Outcome"><c>created</c>, <c>existing</c>, <c>skipped</c> or <c>failed</c>.</param>
/// <param name="Reason">Localized; set for skipped and failed items, and for an add whose tags or read marks failed.</param>
public record MihonImportResult(string ProviderId, string Outcome, int? SeriesId, string? Reason);

public record MihonImportResponse(IReadOnlyList<MihonImportResult> Results);

/// <summary>
/// Brings a Mihon library into Maki: resolves each manga to a MangaBaka id, then adds it through
/// <see cref="SeriesCreationService"/> like an import list does, with its categories as user tags
/// and its read chapters as read state. Stateless between calls; the client drives the batches.
/// </summary>
public class MihonImportService(
    MakiDbContext db,
    MangaBakaLocalStore store,
    IEnumerable<IMetadataProvider> metadataProviders,
    SeriesCreationService creation,
    TagService tags,
    ReadingProgressService progress,
    PendingReadImportService pendingReads,
    ICurrentUser currentUser,
    ILocalizer localizer,
    ILogger<MihonImportService> logger)
{
    public const string AddedFrom = "mihon";

    /// <summary>Title search results scored per manga, and the most offered for review.</summary>
    public const int CandidateLimit = 5;

    public static Guid MutationId(int userId, string runId, string providerId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"mihon:{userId}:{runId}:{providerId}")).AsSpan(0, 16));

    public Task<bool> DumpAvailableAsync(CancellationToken ct) => store.IsAvailableAsync(ct);

    /// <summary>Same rule as the library import: the folder has to exist and be one the caller was granted.</summary>
    public async Task<bool> CanUseRootFolderAsync(int rootFolderId, CancellationToken ct) =>
        (currentUser.AllRootFolders || currentUser.RootFolderIds.Contains(rootFolderId)) &&
        await db.RootFolders.AnyAsync(r => r.Id == rootFolderId, ct);

    public async Task<IReadOnlyList<MihonResolveResult>> ResolveAsync(
        IReadOnlyList<MihonResolveItem> items, CancellationToken ct)
    {
        var matches = new (MetadataSearchResult Match, string By)?[items.Count];

        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].MangaBakaId is { } raw && long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
                id > 0 && await LookupAsync(id, ct) is { } hit)
            {
                matches[i] = (hit, "mangabaka");
            }
        }

        await ByExternalIdAsync(MangaBakaLocalStore.ExternalSource.AniList, x => x.AniListId, "anilist");
        await ByExternalIdAsync(MangaBakaLocalStore.ExternalSource.MyAnimeList, x => x.MalId, "mal");
        await ByExternalIdAsync(MangaBakaLocalStore.ExternalSource.Kitsu, x => x.KitsuId, "kitsu");

        var candidates = new IReadOnlyList<MetadataSearchResult>[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            candidates[i] = [];
            if (matches[i] is null && !string.IsNullOrWhiteSpace(items[i].Title))
            {
                (matches[i], candidates[i]) = await ByTitleAsync(items[i].Title, ct);
            }
        }

        var library = await LibraryStatusAsync(
            matches.OfType<(MetadataSearchResult Match, string By)>().Select(m => m.Match.ProviderId), ct);

        return items.Select((item, i) =>
        {
            if (matches[i] is not { } m)
            {
                return new MihonResolveResult(item.Index, candidates[i].Count > 0 ? "needsReview" : "notFound",
                    null, null, candidates[i], "none", null);
            }

            var (where, seriesId) = library.GetValueOrDefault(m.Match.ProviderId, ("none", (int?)null));
            return new MihonResolveResult(item.Index, "matched", m.By, m.Match, [], where, seriesId);
        }).ToList();

        async Task ByExternalIdAsync(
            MangaBakaLocalStore.ExternalSource source, Func<MihonResolveItem, int?> key, string by)
        {
            var open = Enumerable.Range(0, items.Count).Where(i => matches[i] is null && key(items[i]) is > 0).ToList();
            if (open.Count == 0)
            {
                return;
            }

            var map = await store.GetIdsByExternalIdsAsync(
                source, open.Select(i => (long)key(items[i])!.Value).Distinct().ToList(), ct);
            foreach (var i in open)
            {
                if (map.TryGetValue(key(items[i])!.Value, out var mangaBakaId) &&
                    await LookupAsync(mangaBakaId, ct) is { } hit)
                {
                    matches[i] = (hit, by);
                }
            }
        }
    }

    private async Task<MetadataSearchResult?> LookupAsync(long mangaBakaId, CancellationToken ct) =>
        await store.GetAsync(mangaBakaId.ToString(CultureInfo.InvariantCulture), ct) is { } m
            ? new MetadataSearchResult(m.ProviderId, m.Title, m.CoverUrl, m.Year, m.Status, m.Description, m.TotalChapters)
            : null;

    /// <summary>
    /// Scores the first few search hits against every title the dump holds for them. Accepted only
    /// when exactly one passes <see cref="ScrobbleMatching.MatchThreshold"/> at the top score: two
    /// works sharing a title are a question for the user, not a coin toss.
    /// </summary>
    private async Task<((MetadataSearchResult, string)? Match, IReadOnlyList<MetadataSearchResult> Candidates)> ByTitleAsync(
        string title, CancellationToken ct)
    {
        var hits = (await metadataProviders.First().SearchAsync(title, currentUser.MaxContentRating, ct))
            .Take(CandidateLimit)
            .ToList();
        if (hits.Count == 0)
        {
            return (null, []);
        }

        var scores = new List<double>(hits.Count);
        foreach (var hit in hits)
        {
            var names = new List<string> { hit.Title };
            if (await store.GetAsync(hit.ProviderId, ct) is { } meta)
            {
                names.Add(meta.Title);
                if (meta.OriginalTitle is { } original) names.Add(original);
                names.AddRange(meta.AltTitles.Select(a => a.Title));
            }

            scores.Add(names.Max(n => ScrobbleMatching.MatchScore([title], n)));
        }

        var best = scores.Max();
        if (best >= ScrobbleMatching.MatchThreshold && scores.Count(s => s == best) == 1)
        {
            return ((hits[scores.IndexOf(best)], "title"), []);
        }

        return (null, hits);
    }

    /// <summary>
    /// Where each MangaBaka id already lives: a series the caller can see, or one only in a root
    /// folder they were not granted. Two queries, so the second never reveals which series it is.
    /// </summary>
    private async Task<Dictionary<string, (string Where, int? SeriesId)>> LibraryStatusAsync(
        IEnumerable<string> providerIds, CancellationToken ct)
    {
        var ids = providerIds
            .Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        var result = new Dictionary<string, (string, int?)>();
        if (ids.Count == 0)
        {
            return result;
        }

        var visible = await db.Series
            .Where(s => s.MangaBakaId != null && ids.Contains(s.MangaBakaId.Value))
            .Select(s => new { s.Id, MangaBakaId = s.MangaBakaId!.Value })
            .ToListAsync(ct);
        foreach (var s in visible.OrderBy(s => s.Id))
        {
            result.TryAdd(s.MangaBakaId.ToString(CultureInfo.InvariantCulture), ("visible", s.Id));
        }

        var seen = visible.Select(s => s.MangaBakaId).ToHashSet();
        var elsewhere = await db.Series.IgnoreQueryFilters()
            .Where(s => s.MangaBakaId != null && ids.Contains(s.MangaBakaId.Value) && !seen.Contains(s.MangaBakaId.Value))
            .Select(s => s.MangaBakaId!.Value)
            .Distinct()
            .ToListAsync(ct);
        foreach (var id in elsewhere)
        {
            result.TryAdd(id.ToString(CultureInfo.InvariantCulture), ("elsewhere", null));
        }

        return result;
    }

    /// <summary>
    /// Adds each item in turn. Sequential on purpose: two Mihon sources of one work can resolve to
    /// one MangaBaka id even after the client grouped them, and the second then lands on the
    /// existing-series branch instead of racing the first.
    /// </summary>
    public async Task<IReadOnlyList<MihonImportResult>> ImportAsync(MihonImportRequest request, CancellationToken ct)
    {
        var results = new List<MihonImportResult>();
        var canTag = request.ApplyTags && currentUser.Has(MakiPermission.ManageTags);
        foreach (var item in request.Items ?? [])
        {
            try
            {
                results.Add(await ImportOneAsync(request, item, canTag, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Mihon import of MangaBaka {ProviderId} failed for user {UserId}",
                    item.ProviderId, currentUser.UserId);
                db.ChangeTracker.Clear();
                results.Add(new MihonImportResult(item.ProviderId, "failed", null, localizer.Get("error.mihon.importFailed")));
            }
        }

        return results;
    }

    private async Task<MihonImportResult> ImportOneAsync(
        MihonImportRequest request, MihonImportItem item, bool canTag, CancellationToken ct)
    {
        if (!int.TryParse(item.ProviderId, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            return Failed("error.series.metadataNotFound");
        }

        var result = await creation.CreateAsync(
            item.ProviderId, request.RootFolderId, request.Monitored, request.MonitorNewItems, ct,
            deferSourceMatching: true,
            matchLane: SourceMatchLane.Background,
            attributedUserId: currentUser.UserId,
            addedFrom: AddedFrom,
            clientMutationId: MutationId(currentUser.UserId, request.RunId, item.ProviderId));

        int seriesId;
        string title;
        string outcome;
        if (result.Series is { } created)
        {
            (seriesId, title, outcome) = (created.Id, created.Title, "created");
        }
        else if (result.Error == SeriesCreationError.AlreadyInLibrary && result.ExistingSeriesId is { } existingId)
        {
            if (await VisibleCopyAsync(existingId, ct) is not { } visible)
            {
                return new MihonImportResult(item.ProviderId, "skipped", null, localizer.Get("error.mihon.inLibraryElsewhere"));
            }

            (seriesId, title, outcome) = (visible.Id, visible.Title, "existing");
        }
        else
        {
            return result.Error switch
            {
                SeriesCreationError.OperationResultGone =>
                    new MihonImportResult(item.ProviderId, "skipped", null, localizer.Get("error.series.addResultGone")),
                SeriesCreationError.MutationIdReused => Failed("error.series.mutationIdReused"),
                SeriesCreationError.RootFolderNotFound => Failed("error.series.rootFolderNotFound"),
                _ => Failed("error.series.metadataNotFound"),
            };
        }

        string? reason = null;
        try
        {
            if (canTag)
            {
                await ApplyTagsAsync(seriesId, item.Categories ?? [], ct);
            }

            if (request.ApplyReadState)
            {
                await ApplyReadStateAsync(seriesId, title, item, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Tags or read marks from a Mihon backup could not be applied to series {SeriesId}", seriesId);
            db.ChangeTracker.Clear();
            reason = localizer.Get("error.mihon.extrasFailed");
        }

        return new MihonImportResult(item.ProviderId, outcome, seriesId, reason);

        MihonImportResult Failed(string key) => new(item.ProviderId, "failed", null, localizer.Get(key));
    }

    /// <summary>
    /// A copy of the same work the caller can see. <see cref="SeriesCreationService"/> reports the
    /// first copy it finds with filters off, which may sit in a folder this caller was not granted
    /// while another copy is in one they were.
    /// </summary>
    private async Task<(int Id, string Title)?> VisibleCopyAsync(int existingId, CancellationToken ct)
    {
        var mangaBakaId = await db.Series.IgnoreQueryFilters()
            .Where(s => s.Id == existingId)
            .Select(s => s.MangaBakaId)
            .FirstOrDefaultAsync(ct);
        var visible = await db.Series
            .Where(s => s.Id == existingId || (mangaBakaId != null && s.MangaBakaId == mangaBakaId))
            .OrderBy(s => s.Id)
            .Select(s => new { s.Id, s.Title })
            .FirstOrDefaultAsync(ct);
        return visible is null ? null : (visible.Id, visible.Title);
    }

    private async Task ApplyTagsAsync(int seriesId, IEnumerable<string> categories, CancellationToken ct)
    {
        var labels = categories
            .Select(c => c.Trim())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (labels.Count == 0)
        {
            return;
        }

        var ids = new List<int>(labels.Count);
        foreach (var label in labels)
        {
            ids.Add((await tags.FindOrCreateAsync(label, null, ct)).Tag.Id);
        }

        await tags.AddToSeriesAsync(seriesId, ids, ct);
    }

    /// <summary>
    /// Raises the silent mark to the furthest read chapter (or the tracker's number when the backup
    /// had no chapter rows for it), then stores the numbers to tick off once chapters exist.
    /// </summary>
    private async Task ApplyReadStateAsync(int seriesId, string title, MihonImportItem item, CancellationToken ct)
    {
        var numbers = (item.ReadNumbers ?? []).Where(n => n >= 0).Distinct().ToList();
        var mark = numbers.Count > 0 ? numbers.Max() : item.TrackerLastRead;
        if (mark is > 0)
        {
            await progress.ImportSilentAsync(currentUser.UserId, seriesId, null, title, (double)mark.Value, 0, ct);
        }

        if (numbers.Count == 0)
        {
            return;
        }

        await pendingReads.AddAsync(currentUser.UserId, seriesId, numbers, ct);
        if (await db.Chapters.IgnoreQueryFilters().AnyAsync(c => c.SeriesId == seriesId, ct))
        {
            await pendingReads.ApplyAsync(seriesId, ct);
        }
    }
}
