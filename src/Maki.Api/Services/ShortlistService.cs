using Maki.Core.Configuration;
using Maki.Core.Entities;
using Maki.Core.Security;
using Maki.Data;
using Maki.Metadata.MangaBaka;
using Microsoft.EntityFrameworkCore;

namespace Maki.Api.Services;

public sealed record ShortlistStatusChange(SeriesStatus From, SeriesStatus To);

/// <summary>Null <paramref name="SeriesId"/>: in a root folder the caller cannot see.</summary>
public sealed record ShortlistLibrary(int? SeriesId, bool Archived);

/// <param name="MangaBakaId">The stored key, which list-row writes address.</param>
/// <param name="Title">The title as stored, for a row with no card.</param>
/// <param name="Item">The canonical work's card, or null when the dump has no usable row.</param>
/// <param name="Type">The canonical row's <c>type</c>, set with the card.</param>
/// <param name="HasAnime">The canonical row has an anime range (<c>anime_start</c> or <c>anime_end</c>).</param>
public sealed record ShortlistListItem(
    int MangaBakaId,
    string Title,
    MangaBakaRecommendation? Item,
    string? Type,
    bool HasAnime,
    string? Note,
    string SavedFrom,
    string? SavedFromDetail,
    DateTime CreatedAtUtc,
    ShortlistStatusChange? StatusChange,
    int? NewChapters,
    ShortlistLibrary? Library);

/// <summary>
/// The list. With no dump installed <paramref name="CatalogueAvailable"/> is false and the list is
/// empty: stored titles cannot be checked against the ceiling or the never-show list without it.
/// </summary>
public sealed record ShortlistList(bool CatalogueAvailable, IReadOnlyList<ShortlistListItem> Entries);

/// <param name="Ids">Each visible entry under its stored key and, when merged, its canonical id too.</param>
/// <param name="Count">Visible entries, a merged one counted once.</param>
public sealed record ShortlistIds(IReadOnlyList<int> Ids, int Count);

/// <summary>An entry as it is stored, which DELETE returns and restore takes back.</summary>
public sealed record ShortlistEntryDto(
    int MangaBakaId,
    string Title,
    string? Note,
    string SavedFrom,
    string? SavedFromDetail,
    SeriesStatus? SavedStatus,
    int? SavedTotalChapters,
    DateTime CreatedAtUtc)
{
    public static ShortlistEntryDto From(ShortlistEntry e) => new(
        e.MangaBakaId, e.Title, e.Note, e.SavedFrom, e.SavedFromDetail, e.SavedStatus,
        e.SavedTotalChapters, e.CreatedAtUtc);
}

public enum ShortlistInsertOutcome { Inserted, Existing, NotInCatalogue, InLibrary, Full }

public sealed record ShortlistInsertResult(ShortlistInsertOutcome Outcome, ShortlistEntry? Entry = null);

/// <summary>
/// The per-user shortlist: catalogue titles saved for later, with nothing else happening. Never a
/// taste signal, so no write here touches <see cref="RecommendationFeedback"/> or its revisions.
/// </summary>
public class ShortlistService(
    MakiDbContext db, MangaBakaLocalStore store, ICurrentUser currentUser, HiddenContentService hidden)
{
    public const string FromImportList = "importlist";
    public const string FromOther = "other";

    private static readonly HashSet<string> PlainSources = new(StringComparer.Ordinal)
    {
        "search", "catalogue", "creator", "series", "anime", FromImportList, FromOther,
    };

    private sealed record Visible(ShortlistEntry Entry, long CanonicalId, MangaBakaShortlistRow Row)
    {
        public MangaBakaRecommendation? Item => Row.Item;
    }

    public async Task<ShortlistIds> IdsAsync(CancellationToken ct = default)
    {
        var visible = await VisibleAsync(ct);
        var ids = new List<int>();
        foreach (var v in visible)
        {
            ids.Add(v.Entry.MangaBakaId);
            if (v.CanonicalId != v.Entry.MangaBakaId && v.CanonicalId is > 0 and <= int.MaxValue)
            {
                ids.Add((int)v.CanonicalId);
            }
        }

        return new ShortlistIds(ids.Distinct().ToList(), visible.Count);
    }

    public async Task<ShortlistList> ListAsync(CancellationToken ct = default)
    {
        if (!await store.IsAvailableAsync(ct))
        {
            return new ShortlistList(false, []);
        }

        var visible = await VisibleAsync(ct);
        var canonical = visible.Select(v => v.CanonicalId)
            .Where(id => id is > 0 and <= int.MaxValue).Select(id => (int?)(int)id).Distinct().ToList();
        // Unfiltered, archived included: the add refuses either, so either is "in the library".
        var owned = await db.Series.IgnoreQueryFilters().AsNoTracking()
            .Where(s => canonical.Contains(s.MangaBakaId))
            .Select(s => new { s.Id, s.MangaBakaId, Archived = s.ArchivedAt != null })
            .ToListAsync(ct);
        var seen = (await db.Series.AsNoTracking()
            .Where(s => canonical.Contains(s.MangaBakaId))
            .Select(s => s.Id).ToListAsync(ct)).ToHashSet();

        return new ShortlistList(true, visible
            .OrderByDescending(v => v.Entry.CreatedAtUtc).ThenByDescending(v => v.Entry.Id)
            .Select(v =>
            {
                var copies = owned.Where(s => s.MangaBakaId == v.CanonicalId).ToList();
                var copy = copies.FirstOrDefault(s => seen.Contains(s.Id)) ?? copies.FirstOrDefault();
                var library = copy is null
                    ? null
                    : new ShortlistLibrary(seen.Contains(copy.Id) ? copy.Id : null, copy.Archived);
                return new ShortlistListItem(
                    v.Entry.MangaBakaId, v.Entry.Title, v.Item, v.Row.Type, v.Row.HasAnime, v.Entry.Note,
                    v.Entry.SavedFrom, v.Entry.SavedFromDetail, v.Entry.CreatedAtUtc, StatusChange(v.Entry, v.Item),
                    NewChapters(v.Entry, v.Item), library);
            })
            .ToList());
    }

    /// <summary>
    /// Saves a title for the caller. A repeat changes only the note, and only when one is sent; the
    /// snapshot and provenance stay as they were first saved.
    /// </summary>
    public async Task<ShortlistInsertResult> SaveAsync(
        int mangaBakaId, string? savedFrom, string? savedFromDetail, string? note, CancellationToken ct = default)
    {
        using var _ = await SeriesLocks.ProviderIdAsync(mangaBakaId, ct);
        if (await FindAsync(mangaBakaId, ct) is { } existing)
        {
            if (note is not null)
            {
                existing.Note = NormalizeNote(note);
                existing.UpdatedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }

            return new ShortlistInsertResult(ShortlistInsertOutcome.Existing, existing);
        }

        return await InsertUnlockedAsync(currentUser.UserId, Allowed, mangaBakaId,
            new ShortlistEntry
            {
                SavedFrom = NormalizeSource(savedFrom),
                SavedFromDetail = Cap(savedFromDetail, ShortlistEntry.SavedFromDetailMaxLength),
                Note = note is null ? null : NormalizeNote(note),
            }, keepSnapshot: false, ct);
    }

    /// <summary>
    /// Inserts for an explicit user, for callers with no signed-in reader (the import-list job runs
    /// unrestricted, so neither the scope nor <see cref="ICurrentUser"/> is the list owner's).
    /// An existing entry for the id is returned unchanged, including one stored under a merged id.
    /// </summary>
    /// <param name="mergedKeys">
    /// <see cref="MergedKeysAsync"/> for the user, so a caller inserting many ids resolves the stored
    /// keys once. Resolved here when null.
    /// </param>
    public async Task<ShortlistInsertResult> InsertAsync(
        int userId, IReadOnlyList<string> allowedRatings, int mangaBakaId, string? savedFrom,
        string? savedFromDetail, string? note = null, Dictionary<int, int>? mergedKeys = null,
        CancellationToken ct = default)
    {
        if (mangaBakaId <= 0)
        {
            return new ShortlistInsertResult(ShortlistInsertOutcome.NotInCatalogue);
        }

        using var _ = await SeriesLocks.ProviderIdAsync(mangaBakaId, ct);
        var existing = await Entries(userId).FirstOrDefaultAsync(x => x.MangaBakaId == mangaBakaId, ct);
        var canonical = mangaBakaId;
        if (existing is null)
        {
            mergedKeys ??= await MergedKeysAsync(userId, ct);
            if ((await store.ResolveCanonicalIdsAsync([mangaBakaId], ct)).TryGetValue(mangaBakaId, out var resolved)
                && resolved is > 0 and <= int.MaxValue)
            {
                canonical = (int)resolved;
            }

            var storedKeys = mergedKeys.Where(m => m.Value == mangaBakaId || m.Value == canonical)
                .Select(m => m.Key).ToList();
            if (canonical != mangaBakaId)
            {
                storedKeys.Add(canonical);
            }

            if (storedKeys.Count > 0)
            {
                existing = await Entries(userId).FirstOrDefaultAsync(x => storedKeys.Contains(x.MangaBakaId), ct);
            }
        }

        if (existing is not null)
        {
            return new ShortlistInsertResult(ShortlistInsertOutcome.Existing, existing);
        }

        var result = await InsertUnlockedAsync(userId, allowedRatings, mangaBakaId,
            new ShortlistEntry
            {
                SavedFrom = NormalizeSource(savedFrom),
                SavedFromDetail = Cap(savedFromDetail, ShortlistEntry.SavedFromDetailMaxLength),
                Note = note is null ? null : NormalizeNote(note),
            }, keepSnapshot: false, ct);

        // The caller's map is reused for its next insert, so a later id for the same work finds this one.
        if (result.Outcome == ShortlistInsertOutcome.Inserted && canonical != mangaBakaId)
        {
            mergedKeys![mangaBakaId] = canonical;
        }

        return result;
    }

    /// <summary>
    /// The user's stored keys that a dump merge has moved, each with its canonical id. Resolution
    /// only: no ceiling and no card.
    /// </summary>
    public async Task<Dictionary<int, int>> MergedKeysAsync(int userId, CancellationToken ct = default)
    {
        var keys = await Entries(userId).AsNoTracking().Select(x => (long)x.MangaBakaId).ToListAsync(ct);
        if (keys.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        return (await store.ResolveCanonicalIdsAsync(keys, ct))
            .Where(r => r.Value != r.Key && r.Value is > 0 and <= int.MaxValue)
            .ToDictionary(r => (int)r.Key, r => (int)r.Value);
    }

    /// <summary>
    /// Undo for a removal: reinserts the entry as DELETE returned it, snapshot and provenance included,
    /// under the same rules as a save. The body is client-supplied, so every field is validated again.
    /// </summary>
    public async Task<ShortlistInsertResult> RestoreAsync(
        int mangaBakaId, ShortlistEntryDto body, CancellationToken ct = default)
    {
        using var _ = await SeriesLocks.ProviderIdAsync(mangaBakaId, ct);
        if (await FindAsync(mangaBakaId, ct) is { } existing)
        {
            return new ShortlistInsertResult(ShortlistInsertOutcome.Existing, existing);
        }

        var now = DateTime.UtcNow;
        var created = body.CreatedAtUtc == default
            ? now
            : body.CreatedAtUtc.Kind == DateTimeKind.Local ? body.CreatedAtUtc.ToUniversalTime() : body.CreatedAtUtc;
        return await InsertUnlockedAsync(currentUser.UserId, Allowed, mangaBakaId,
            new ShortlistEntry
            {
                Title = Cap(body.Title, ShortlistEntry.TitleMaxLength) ?? "",
                Note = body.Note is null ? null : NormalizeNote(body.Note),
                SavedFrom = NormalizeSource(body.SavedFrom),
                SavedFromDetail = Cap(body.SavedFromDetail, ShortlistEntry.SavedFromDetailMaxLength),
                SavedStatus = body.SavedStatus is { } status && Enum.IsDefined(status) ? status : null,
                SavedTotalChapters = body.SavedTotalChapters is >= 0 ? body.SavedTotalChapters : null,
                CreatedAtUtc = DateTime.SpecifyKind(created > now ? now : created, DateTimeKind.Utc),
            }, keepSnapshot: true, ct);
    }

    public async Task<ShortlistEntry?> UpdateNoteAsync(int mangaBakaId, string? note, CancellationToken ct = default)
    {
        if (await FindAsync(mangaBakaId, ct) is not { } entry)
        {
            return null;
        }

        entry.Note = NormalizeNote(note);
        entry.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return entry;
    }

    /// <summary>"Got it": the snapshot becomes the current dump row, so the change stops showing.</summary>
    public async Task<ShortlistEntry?> AcknowledgeAsync(int mangaBakaId, CancellationToken ct = default)
    {
        if (await FindAsync(mangaBakaId, ct) is not { } entry)
        {
            return null;
        }

        var rows = await store.GetShortlistRowsAsync([entry.MangaBakaId], Allowed, ct);
        if (rows.GetValueOrDefault(entry.MangaBakaId)?.Item is { } item)
        {
            entry.SavedStatus = Snapshot(item.Status);
            entry.SavedTotalChapters = item.TotalChapters;
            entry.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        return entry;
    }

    public async Task<ShortlistEntry?> RemoveAsync(int mangaBakaId, CancellationToken ct = default)
    {
        if (await FindAsync(mangaBakaId, ct) is not { } entry)
        {
            return null;
        }

        db.ShortlistEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
        return entry;
    }

    /// <summary>
    /// The caller's entries the list and the ids both show. Above the ceiling or on the never-show
    /// list is left out but left stored, so raising the ceiling brings an entry back. Reads only.
    /// </summary>
    private async Task<IReadOnlyList<Visible>> VisibleAsync(CancellationToken ct)
    {
        var entries = await Entries(currentUser.UserId).AsNoTracking().ToListAsync(ct);
        if (entries.Count == 0 || !await store.IsAvailableAsync(ct))
        {
            return [];
        }

        var rows = await store.GetShortlistRowsAsync(entries.Select(e => (long)e.MangaBakaId).ToList(), Allowed, ct);
        var kept = entries
            .Where(e => rows.ContainsKey(e.MangaBakaId))
            .Select(e => new Visible(e, rows[e.MangaBakaId].CanonicalId, rows[e.MangaBakaId]))
            .ToList();
        // On the canonical ids: the loaded vector index has no row for a merged key and would never hide it.
        var isHidden = await hidden.PredicateAsync(kept.Select(v => v.CanonicalId).Distinct(), ct);
        return isHidden is null ? kept : kept.Where(v => !isHidden(v.CanonicalId)).ToList();
    }

    /// <summary>
    /// The caller's entry for an id, tracked: stored under that id, or else stored under a merged id
    /// whose canonical work is this one. Cards and the modal only know the canonical id.
    /// </summary>
    private async Task<ShortlistEntry?> FindAsync(int mangaBakaId, CancellationToken ct)
    {
        var userId = currentUser.UserId;
        if (await Entries(userId).FirstOrDefaultAsync(x => x.MangaBakaId == mangaBakaId, ct) is { } stored)
        {
            return stored;
        }

        var merged = (await VisibleAsync(ct))
            .FirstOrDefault(v => v.CanonicalId == mangaBakaId && v.Entry.MangaBakaId != mangaBakaId);
        return merged is null
            ? null
            : await Entries(userId).FirstOrDefaultAsync(x => x.Id == merged.Entry.Id, ct);
    }

    /// <summary>Called with the provider-id lock held and no existing entry found.</summary>
    private async Task<ShortlistInsertResult> InsertUnlockedAsync(
        int userId, IReadOnlyList<string> allowed, int mangaBakaId, ShortlistEntry entry, bool keepSnapshot,
        CancellationToken ct)
    {
        if (userId <= 0)
        {
            return new ShortlistInsertResult(ShortlistInsertOutcome.NotInCatalogue);
        }

        // One answer for "no such row" and "above your ceiling", so a 404 does not confirm a title exists.
        var row = (await store.GetShortlistRowsAsync([mangaBakaId], allowed, ct)).GetValueOrDefault(mangaBakaId);
        if (row?.Item is not { } item)
        {
            return new ShortlistInsertResult(ShortlistInsertOutcome.NotInCatalogue);
        }

        int? canonical = row.CanonicalId is > 0 and <= int.MaxValue ? (int)row.CanonicalId : null;
        if (await db.Series.IgnoreQueryFilters()
                .AnyAsync(s => s.MangaBakaId == mangaBakaId || (canonical != null && s.MangaBakaId == canonical), ct))
        {
            return new ShortlistInsertResult(ShortlistInsertOutcome.InLibrary);
        }

        if (await Entries(userId).CountAsync(ct) >= ShortlistEntry.MaxPerUser)
        {
            return new ShortlistInsertResult(ShortlistInsertOutcome.Full);
        }

        var now = DateTime.UtcNow;
        entry.UserId = userId;
        entry.MangaBakaId = mangaBakaId;
        if (!keepSnapshot)
        {
            entry.SavedStatus = Snapshot(item.Status);
            entry.SavedTotalChapters = item.TotalChapters;
            entry.CreatedAtUtc = now;
        }

        if (string.IsNullOrWhiteSpace(entry.Title))
        {
            entry.Title = Cap(item.Title, ShortlistEntry.TitleMaxLength) ?? "";
        }

        entry.UpdatedAtUtc = now;
        db.ShortlistEntries.Add(entry);
        await db.SaveChangesAsync(ct);
        return new ShortlistInsertResult(ShortlistInsertOutcome.Inserted, entry);
    }

    /// <summary>
    /// One user's rows. The explicit id keeps an unrestricted scope (a job) to that user, and
    /// <see cref="UserScopedQuery.OwnedByScopeUser{T}"/> lets SQLite seek the unique index.
    /// </summary>
    private IQueryable<ShortlistEntry> Entries(int userId) =>
        db.ShortlistEntries.OwnedByScopeUser(db).Where(x => x.UserId == userId);

    private IReadOnlyList<string> Allowed => ContentRating.Allowed(currentUser.MaxContentRating);

    private static ShortlistStatusChange? StatusChange(ShortlistEntry entry, MangaBakaRecommendation? item) =>
        item is not null && entry.SavedStatus is { } saved && item.Status != SeriesStatus.Unknown && saved != item.Status
            ? new ShortlistStatusChange(saved, item.Status)
            : null;

    private static int? NewChapters(ShortlistEntry entry, MangaBakaRecommendation? item) =>
        item?.TotalChapters is { } now && entry.SavedTotalChapters is { } then && now > then ? now - then : null;

    /// <summary>Unknown is stored as no snapshot, so a status the dump fills in later is not a change.</summary>
    private static SeriesStatus? Snapshot(SeriesStatus status) => status == SeriesStatus.Unknown ? null : status;

    public static string NormalizeSource(string? savedFrom)
    {
        var value = savedFrom?.Trim();
        if (value is null)
        {
            return FromOther;
        }

        if (PlainSources.Contains(value))
        {
            return value;
        }

        if (value.StartsWith(LayoutRails.Prefix, StringComparison.Ordinal))
        {
            var key = value[LayoutRails.Prefix.Length..];
            if (DiscoverSections.All.Contains(key) || HomeSections.All.Contains(key) ||
                LayoutRails.TryParse(value, out _))
            {
                return value;
            }
        }

        return FromOther;
    }

    private static string? NormalizeNote(string? note) => Cap(note, ShortlistEntry.NoteMaxLength);

    private static string? Cap(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= max ? trimmed : trimmed[..max].TrimEnd();
    }
}
