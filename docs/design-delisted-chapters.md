# Delisted chapters

Plan written 2026-11-04 for issue #163 ("Flag a chapter that no enabled source lists any more"). Checked against `dev` at `c913f935`, then reviewed against the code the same day; the review's findings are folded in. Built on `feat/delisted-chapters` on 2026-11-05.

## Verdict on the issue

Worth adding. The issue describes the code correctly in most places, and the change is small: one nullable column, one pass at the end of chapter sync, a badge, a filter chip, a series-page alert and one inbox row. It deletes nothing and changes no download behaviour, so a bug in it can only mislabel a row.

Five places where the plan departs from the issue as written:

1. **The stamp is decided after every mapping has synced, not inside `ReplaceSnapshot`.** `ReplaceSnapshot` runs once per mapping. If mapping A drops a chapter and mapping B (synced later in the same loop) adds it, a per-mapping stamp would set and then clear `DelistedAt` in one sync, and the inbox count would be wrong. One pass after the loop sees the final link set. `ReplaceSnapshot` itself does not change.
2. **Sources that only ever list a free window are left out.** ComicWalker ("the chapter list only ever shows the free window: the first couple plus the newest few") and the GigaViewer sites (Shonen Jump+, Comic Days and the rest: "free episodes only") drop chapters from their listing every week by design. Without an exception, every chapter downloaded from them while it was free would turn Delisted, with an inbox row each week. These sources get a capability flag, and a link lost from one of them never delists a chapter.
3. **"A chapter with no file just stops being queued" is not true today.** A wanted chapter with no file is still `Chapter.IsMissing`, so Smart, "Download next N" and "Download N wanted" still queue it, and the resolver then fails it as `error.download.notListed` until the retry cap. That is current behaviour, not something this feature causes. This plan leaves download selection alone (see Open decisions).
4. **The series-page count is worked out on the client.** The series page already loads every chapter, so the alert counts `delistedAt` rows from that list instead of adding a field to `SeriesDto` and another `COUNT` to `ChapterTalliesAsync`.
5. **A "Keep" action clears the flag.** MangaDex removes chapters after licence takedowns, and then the file is fine. Without a way to dismiss it, the series alert would sit there for good. Clearing is safe: the stamp is only ever set when a link is lost, so a cleared chapter with no links is never stamped again.

The issue's side suggestion (hold a single new chapter numbered far above every other mapping, like the bulk hold) stays out of scope, as the issue itself says.

## What "delisted" means

A chapter is delisted when, at the start of a sync, at least one **live** mapping linked it, and at the end of that sync no live mapping does, and at least one of the links it lost came from a source that does not list a free window only.

- **Live** is the same set the sync loop uses: `SourceMapping.Enabled` and the source not switched off in `SourceAvailability`. A link held by a disabled mapping neither keeps a chapter listed nor clears a stamp. That matches the issue title ("no enabled source").
- A chapter **never linked** by a live mapping is never delisted. That covers `ChapterFromFilesService` rows, volume entries, rows from a mapping the user since removed, and every row that was already linkless before this ships. No backfill: there is no way to tell those apart after the fact.
- **Removing a mapping does not delist.** `SourceMappingRemovalService` deletes the links through the FK, outside sync, and the next sync sees no live link at its start. The user removed the source on purpose.
- **Changing a mapping's language filter does not delist.** `SourceMappingController.Update` keeps the mapping's links and only nulls `ChapterSnapshotAt` ("the stored links describe the old filter"). The next sync lists the new language set and `ReplaceSnapshot` drops every link of a language the user removed. Those links are not counted at the start of the sync (see below), so the chapters of the dropped language are not stamped.
- A mapping whose listing **fails** keeps its links (the empty-listing guard and the `catch` both leave `ChapterLinks` alone), so a source outage never delists anything.
- The stamp is **cleared** whenever a live mapping links the chapter again, at the end of any sync, and by the Keep action.

Nothing else changes on the row: `Wanted`, the file, reads, counts and the denominator in `ChapterTalliesAsync` all stay as they are.

## Data model

`Maki.Core/Entities/Chapter.cs`:

```csharp
/// <summary>
/// When a sync left this chapter with no live source link after at least one had listed it, else
/// null. Cleared when a source links it again or the user keeps it. Never set for a chapter no
/// source ever listed. Display only: nothing reads it to decide a download.
/// </summary>
public DateTime? DelistedAt { get; set; }
```

Migration `ChapterDelistedAt`:

```
dotnet ef migrations add ChapterDelistedAt --project src/Maki.Data --startup-project src/Maki.Api
```

No index. Every read is per series and the column is only projected.

`Maki.Core/Sources/ISource.cs`, appended to `SourceCapabilities`:

```csharp
/// <summary>
/// The listing shows only the chapters free to read right now, so chapters leave it on a schedule.
/// A chapter dropped from this source's listing is not counted as delisted.
/// </summary>
FreeWindowListing = 8
```

Declared by `ComicWalkerSource` and `GigaViewerSource` (all its sites inherit it). While implementing, check each official source against its live listing before ruling it in or out. MANGA Plus appears to list its subscriber-only middle chapters too (its `GetPagesAsync` turns their access error into a 404), so it should not need the flag. Webtoons and Naver Webtoon list every episode. Custom and Suwayomi sources stay unflagged.

## Sync

All in `ChapterSyncService.SyncUnderLockAsync`.

**At the start**, after the first `MergeDuplicatesAsync` and before the mapping loop, record which chapters were linked by a live mapping whose links can delist. The live set has to be computed **before** the `mappingIds` narrowing, because a partial `SyncMappingsAsync` still has to count links from mappings it is not refreshing. The free-window question is answered once per mapping here, so the end pass needs no source lookups:

```csharp
var enabledMappingIds = series.SourceMappings
    .Where(m => m.Enabled && !disabledSources.Contains(m.SourceName, StringComparer.OrdinalIgnoreCase))
    .Select(m => m.Id)
    .ToHashSet();
// Links from a free-window source, or from a mapping whose language filter just changed, are
// expected to go and never delist a chapter.
var delistingMappingIds = series.SourceMappings
    .Where(m => enabledMappingIds.Contains(m.Id))
    .Where(m => !(m.ChapterSnapshotAt is null && m.ChapterLinks.Count > 0))
    .Where(m => sourceRegistry.Find(m.SourceName) is not { } s ||
                !s.Capabilities.HasFlag(SourceCapabilities.FreeWindowListing))
    .Select(m => m.Id)
    .ToHashSet();
var listedBefore = existing
    .Where(c => c.Id != 0 && c.SourceLinks.Any(l =>
        db.Entry(l).State != EntityState.Deleted && delistingMappingIds.Contains(l.SourceMappingId)))
    .Select(c => c.Id)
    .ToHashSet();
```

`liveMappings` is then `enabledMappingIds` narrowed by `mappingIds`, as today. The `ChapterSnapshotAt is null && ChapterLinks.Count > 0` test is the state `SourceMappingController.Update` leaves after a language-filter change; a brand-new mapping has no links, so it is not caught by it. The `Deleted` guard changes nothing today (the first merge removes the rows whose links it deletes) but keeps the snapshot right if the order ever changes.

**At the end**, after `DropUnlistedLockedAsync` (so a dropped locked row is not counted) and before the numbering-clash block, a new `MarkDelisted(existing, enabledMappingIds, listedBefore)`:

- For every chapter with `Id != 0` still in `existing`, count its links that are not `EntityState.Deleted` and belong to a mapping in `enabledMappingIds`. New links from this sync have `SourceMappingId` 0 until saved, so read the id through `l.SourceMapping?.Id ?? l.SourceMappingId`, or compare `l.SourceMapping` against the live mapping objects.
- Any live link: set `DelistedAt = null`.
- No live link, `DelistedAt` null and the chapter in `listedBefore`: set `DelistedAt = DateTime.UtcNow`.
- Return the ids it newly stamped that have a `ChapterFileId`, for the notice.

Both full and partial syncs run it. The decision only looks at the chapter's own links, so a partial sync cannot get it wrong the way it could get the numbering clash wrong.

**`MergeDuplicatesAsync`** gets one line beside the `FileRemovedAt` carry-over: `keeper.DelistedAt ??= dup.DelistedAt;`. The end-of-sync pass clears it again if the keeper is linked, so this only matters when neither row is.

**Return value.** `SyncUnderLockAsync` already returns a tuple. Add the newly stamped ids with files to it; `SyncSeriesAsync` raises the notice after the lock is released and keeps returning `List<int>`, so none of its callers change.

## Inbox notice

Raised from `ChapterSyncService`, not `RefreshMonitoredSeriesJob`, so a manual refresh, a re-match and health recovery announce it too. Take `InboxService? inbox = null` as an optional constructor dependency, like the other optional services there.

- New `InboxEventType.ChapterDelisted = 26`, appended after `NoteReply = 25`. Not `IsAdminOnly`, not `IsOperational`, not `DefaultsOff`. `InboxPrefsSpec.Merge` turns it on for existing users.
- One row per series per sync, only when at least one newly stamped chapter has a file: `new InboxMessage(Key: "inbox.chapters.delisted", Level: NotificationLevel.Warning, Params: InboxMessage.Args(new { count }), SeriesId: seriesId, Url: $"/series/{seriesId}")`, audience `InboxAudience.SeriesTrackers(seriesId, series.RootFolderId)`. The bell draws its icon from the level (`NotificationBell.tsx` `LevelIcon`), so Warning gives it the triangle; there is no per-type icon map. No series title in params (`{series}` is filled at read time).
- Catalogue: `inbox.chapters.delisted.title` and `.body` in `locales/en/server.po`, beside the `inbox.chapters.held.*` pair, with a plural on `count`. Suggested English: title "Chapters no longer listed", body "{count, plural, one {# downloaded chapter of {series} is} other {# downloaded chapters of {series} are}} no longer listed by any source."
- No outbound `NotificationEventType`. Discord and webhooks stay for download reports.
- No Web Push by default: `PushPrefs`' unset default is `newChapterAvailable` and `chapterDownloaded`, and this type is not added to it.
- Frontend: `frontend/src/api/inbox.ts` holds all of the inbox wiring: add `'chapterDelisted'` to the type union, to the `library` group of `INBOX_CATEGORIES`, and to the label and description maps. `NotificationsSection.tsx` and `NotificationEvents` in `types.ts` are the outbound events and stay as they are. Check the bell and notifications-page stories for fixtures that list every type.

A source that keeps flapping (listing shrinks, comes back, shrinks again) raises a row each time it shrinks. That is accurate and rare enough to accept; if it shows up in practice, hold the notice until the chapter has been delisted across two syncs.

A source that renumbers its list stamps every old row it numbered differently and announces them all in one row, while the bulk hold catches the new rows. The files are fine in that case, but the count is right and the user should look at the series anyway, so this is accepted rather than special-cased.

## API

`ChapterController.List`: project `c.DelistedAt` in the database query and pass it through in the in-memory `Select`. `ChapterDto` in `frontend/src/api/types.ts` gets:

```ts
/** When the last source that listed this chapter stopped listing it. Null while any enabled source does, or if none ever did. */
delistedAt: string | null
```

New endpoint for Keep, beside `SetWantedBulk` and under the same policy (`Policies.EditMetadata`):

```
POST /api/v1/chapter/delisted/clear   body: { chapterIds: int[] }
```

One `ExecuteUpdateAsync` setting `DelistedAt` to null for the given ids, restricted to series the caller can see (the query filter does this). Returns `{ cleared }`. No series lock: sync only ever sets the column on rows it has just seen lose a link, and a race with it costs one stamp at worst.

## Client

**Chapter row** (`ChapterTableRows.tsx`). Delisted is independent of the file state, so it is an extra badge after the status badge (where `OfflineBadge` goes), not a branch of the Downloaded / Auto-removed / File removed / Missing ternary:

- `LightBadge` with `IconLinkOff`, label `Delisted`. Colour `var(--warn)` when the row has a file (someone should look at it), `var(--neutral)` when it does not.
- Tooltip with the date (`formatCalendarDate`): "No source has listed this chapter since {date}. A source may have filed it here by mistake, so check the file. Sources also drop chapters after a licence takedown, and then the file is fine."

**Filter chip** (`SeriesDetailPage.tsx`). Add `'delisted'` to `CHAPTER_FILTERS` and `delisted: (c) => c.delistedAt != null` to `chapterFilters`. The chip `Delisted ({count})` shows only when the count is above zero, like Skipped. The filter is remembered across series, and `rememberedFilterMatchesNothing` already falls back to All on a series with no delisted chapter, so the effective-filter expression needs no new line.

**Series alert**, in the source panel directly under the numbering-clash `Alert`, shown when at least one delisted chapter has a file:

- Title (plural): "{count} downloaded chapters are no longer listed".
- Body: "Every source that listed them has stopped. Usually a source corrected a listing, so check these files before reading them."
- "Show them" sets the chapter filter to `delisted` and scrolls to the table.
- "Keep them" (only with `EditMetadata`) clears the flag on those chapters through the new endpoint, then invalidates the chapter query.

Delisted chapters without a file get the badge and the chip but no alert.

**Select mode** gets a "Clear delisted" action, shown only when the selection contains a delisted chapter, under `EditMetadata`. Same endpoint.

All copy through `t`/`<Trans>`/`plural`. Run `lingui extract`; do not translate.

## Tests

`tests/Maki.Api.Tests/ChapterSyncServiceTests.cs`:

- One mapping lists 1 to 3, then 1 to 2: chapter 3 is stamped, 1 and 2 are not.
- Two mappings, A drops chapter 3 while B still lists it: not stamped. Then B drops it: stamped.
- A drops chapter 3 and B starts listing it in the same sync: not stamped (the reason for the end-of-sync pass).
- A delisted chapter listed again: cleared.
- A failing mapping (throws, and returns empty with links present): nothing stamped.
- A link on a disabled mapping does not keep a chapter listed; a chapter linked only by a disabled mapping is never stamped.
- A chapter from `ChapterFromFilesService` with no links: never stamped.
- A `FreeWindowListing` source dropping a chapter: not stamped. The same chapter also lost from an ordinary source: stamped. (`FakeSource.Capabilities` is `init`-settable.)
- A language-filter change (`ChapterSnapshotAt` null, links kept) that drops a language: none of that language's chapters is stamped. The sync after that, with the snapshot fresh, stamps normally.
- A partial `SyncMappingsAsync` over mapping A stamps when A drops the chapter's only live link and does not stamp when unsynced mapping B still links it.
- Duplicate merge carries `DelistedAt` when neither row is linked.
- A cleared chapter with no links is not stamped again on the next sync.
- Inbox: one row with the right count when stamped chapters have files, none when none does, none for a chapter already stamped.

Elsewhere: `ChapterController` list returns `delistedAt`; the clear endpoint clears only the given visible ids and refuses without `EditMetadata`. `LocalizationCatalogTests` picks the key up from the literal, but `InboxRendererTests.Messages()` is a hand-written list: add `{ "inbox.chapters.delisted", new { count = 2 } }` there, or the ICU plural is never rendered in a test. Storybook: a delisted row with and without a file in `ChapterTableRows.stories.tsx`, and a `SeriesDetailPage` story with the alert, next to `NumberingClash`.

## Rule file

Add to `.claude/rules/downloads.md`, near the `FileRemovedAt` entry: what `DelistedAt` means, that it is decided once at the end of sync from live links only, that free-window sources never delist, that nothing reads it to decide a download, and that Keep clears it for good. Add `SourceCapabilities.FreeWindowListing` to `src/Maki.Sources/CLAUDE.md` so a new official source gets asked the question.

## Phases

1. Column, migration, capability flag on ComicWalker and GigaViewer, the sync pass, the merge carry-over, sync tests.
2. `ChapterController` projection, `ChapterDto`, badge, filter chip, series alert, clear endpoint and Keep / Clear actions, stories. `delistedAt` is a required field, so the `ChapterDto` fixtures in `ChapterTableRows.stories.tsx` and `OfflineReadingSection.stories.tsx` need it too, or `npx tsc -b` fails.
3. Inbox type, catalogue keys, notice from the sync service, settings and bell wiring, inbox tests.
4. Rule-file updates; check on `.simulated` by turning a mapping off for one series' chapter (drop it from a recorded listing or point a test mapping at a shorter series) and confirming the badge, chip, alert and one inbox row.

Each phase builds and passes on its own. Phase 1 is invisible to users.

## Known gaps

Found in the PR 168 review and accepted for now:

- **The stamp depends on order.** It fires only in the sync where the last counted link goes. If a free-window source or a mapping the user disables later still holds the chapter at that moment, nothing is stamped, and nothing is stamped when that holder lets go afterwards either. A real drop that lands in the same sync as a language-filter change is missed the same way.
- **Suwayomi and custom sources cannot declare `FreeWindowListing`.** A free-window site reached through a Suwayomi extension still gets its chapters flagged as its window closes.

## Open decisions

- **Download selection.** A wanted, delisted chapter with no file still gets queued and fails as `notListed`, and a Smart series waits on it while it is inside its retry backoff. Skipping delisted chapters in `Chapter.NextWanted` would fix the Smart stall, but makes the Missing count include chapters "Download next N" passes over. Recommendation: leave it out of this feature and look again if Smart stalls on delisted chapters in practice.
- **Alert colour.** Warn matches the numbering clash and the case in the issue. If MangaDex takedowns turn out to be most of what it catches, a neutral tone may fit better.
