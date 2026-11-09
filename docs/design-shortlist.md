# Shortlist

Proposal written 2026-11-02 from the mockup at https://claude.ai/artifact/MqzAyYSFxFAz1jboMuusg2, with the owner's decisions from the same day folded in. Reviewed against the code the same day over five passes; every finding is folded in below. Work happens on `feat/shortlist`.

## The problem

Adding a series is a full commitment. `SeriesCreationService.CreateAsync` writes a `Series` row, queues source matching, starts chapter sync, and writes a `UserSeriesState` with `AddedFrom`, which feeds the reader's taste through `SeedWeightService`. A thumbs up is the only lighter mark, and it steers recommendations too (`RecommendationFeedbackPolicy`), and liked titles are then filtered out of every recommendation answer.

So a title that looks good in Discover has two outcomes: leave it and hope it turns up again, or add it. The shortlist is the third: keep it somewhere you can find it, with nothing else happening.

## What a shortlisted title does and doesn't do

| | Shortlist | Library | Thumbs up |
|---|---|---|---|
| Source search, chapter sync, downloads | No | Yes | No |
| Seeds or avoids in recommendations | No | Yes | Yes |
| Stats, Rewind, XP | No | Yes | No |
| Still shown in Discover rails | Yes, marked | As owned | No |
| Home | No | Yes | No |
| Notifications of any kind | No | Per chapter | No |

Owner decisions (2026-11-02):

- Tracker "Plan to read" entries land in the shortlist instead of the library.
- No notifications for shortlisted series. Not on completion, not on new chapters.
- No Home rail.
- Users without `AddSeries` get Request where others get Add to library.
- The icon is the bookmark (`IconBookmark`, `IconBookmarkFilled` when saved), even though saved filters use the outline bookmark too.
- The "how this differs from your library" table is a collapsed disclosure on the empty state, never expanded by default.

## Data model

One new per-user table, `ShortlistEntries`, in `Maki.Core/Entities/ShortlistEntry.cs`:

```csharp
public class ShortlistEntry : IUserOwned
{
    public long Id { get; set; }
    public int UserId { get; set; }
    public int MangaBakaId { get; set; }             // int like Series.MangaBakaId and the provider lock
    public string Title { get; set; } = "";          // display fallback when the dump no longer has the row, max 300
    public string? Note { get; set; }                // trimmed, max 500
    public string SavedFrom { get; set; } = "";      // see "Provenance"
    public string? SavedFromDetail { get; set; }     // max 200
    public SeriesStatus? SavedStatus { get; set; }   // dump status when saved, reset by "Got it"
    public int? SavedTotalChapters { get; set; }     // dump total_chapters when saved, reset by "Got it"
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
```

Configured in `MakiDbContext` the same way as `RecommendationFeedback`: unique index on `(UserId, MangaBakaId)`, FK to `MakiUser` with `OnDelete(Cascade)` so deleting a user deletes their shortlist, and the `_scope.Unrestricted || x.UserId == _scope.UserId` query filter. Migration: `dotnet ef migrations add Shortlist --project src/Maki.Data --startup-project src/Maki.Api`.

Keyed on the MangaBaka id, not a `Series` id, because a shortlisted title has no `Series` row. It is not a column on `RecommendationFeedback`: that row's revision, events and receipts exist to version taste signals, and every write to it bumps `RecommendationProfileState.FeedbackRevision`, which invalidates the recommendation pool cache. A shortlist write must not do that.

A cap of 2000 entries per user, enforced on insert (`error.shortlist.full`). It only exists so a huge tracker list cannot grow the table without bound.

Every per-user read goes through `OwnedByScopeUser` (`UserScopedQuery.cs`) so SQLite seeks the `(UserId, MangaBakaId)` index past the `Unrestricted OR` filter; `shortlist/ids` runs on every page with a rail.

### Provenance

`SavedFrom` is one of a fixed set, checked on write; anything else is stored as `other`:

- `rail:{key}` for a Discover or Home rail, with `{key}` a `DISCOVER_SECTIONS` or `HOME_SECTIONS` key (Home's "You might like" saves as `rail:recommended`, "Currently popular" as `rail:popular`), `rail:{id}` for a custom rail
- `search`, `catalogue`, `creator`, `series` (the series page's "More like this" and "Related" rails), `anime` (Home's "Continue from the anime"), `importlist`, `other`

`SavedFromDetail` carries what the label needs and the key cannot: the creator's name, the tracker label for `importlist`, the custom rail's title at save time. The client renders the label ("Saved 2 months ago from Trending") from `DISCOVER_SECTION_LABELS`, then `HOME_SECTION_LABELS`, then its own map, through `t`. A rail that has since been deleted still has its title in `SavedFromDetail`.

## Changes since saved

The dump refreshes on its own schedule, so a shortlisted title's status and chapter count drift. The list compares the current dump row with the snapshot:

- **Status changed** when `SavedStatus` is set and differs from the current status. The headline case is "Completed since you saved it"; resumed from hiatus and cancelled read the same way with their own words. These rows sort into a "Changed since you saved them" group at the top.
- **New chapters** when both totals are known and the current one is higher. Shown as a quiet "+14 chapters" pill on the row wherever it is. It does not move the row into the changed group: every releasing title gains chapters, so the group would fill with everything.
- **"Got it"** on a changed row resets `SavedStatus` and `SavedTotalChapters` to the current values (`POST shortlist/{id}/acknowledge`), which takes it out of the group and drops the pill.

No job, no notification, no stored "changed" flag. It is computed on read, so it costs nothing when nobody opens the tab.

## API

`ShortlistController`, `[Route("api/v1/shortlist")]`, authenticated, no extra permission (every user who can see Discover can keep a shortlist). All writes are idempotent so the client needs no mutation ids.

| Method | Path | Does |
|---|---|---|
| GET | `shortlist/ids` | `{ ids: int[], count }`, filtered exactly like the list (below), so the tab count and the list always agree. `ids` holds each visible entry's stored key, plus its canonical id when merged; `count` is the number of visible entries, a merged one counted once. Cached by the client: `ids` for the ribbons on every card, `count` for the tab badge. |
| GET | `shortlist` | `{ catalogueAvailable, entries }`, the hydrated list (below). |
| PUT | `shortlist/{mangaBakaId}` | Body `{ savedFrom, savedFromDetail?, note? }`. Upsert. On insert it reads the dump row for the snapshot and title; a repeat PUT changes only the note when one is sent, never the snapshot or the provenance. 404 `error.shortlist.notInCatalogue` when the dump has no active row for the id **or the row is above the caller's content ceiling** (the same answer, so it does not confirm existence, as `RecommendationFeedbackService.MutateAsync` does). 409 `error.shortlist.inLibrary` when a series with that MangaBaka id exists at all (see "In the library" below). |
| PATCH | `shortlist/{mangaBakaId}` | Body `{ note }`. Empty clears it. |
| POST | `shortlist/{mangaBakaId}/acknowledge` | "Got it", see above. |
| DELETE | `shortlist/{mangaBakaId}` | Returns the removed entry (200), or 204 when there was none. |
| POST | `shortlist/{mangaBakaId}/restore` | Body: the entry DELETE returned. Reinserts it as it was, snapshot, provenance and `CreatedAtUtc` included, under the same 404/409/cap rules as PUT. This is what Undo calls, so an accidental removal keeps its "changed since saved" state. When an entry for the id already exists (Undo racing a re-save in another tab) it returns 200 with that entry and writes nothing. |

PUT's and restore's "not in catalogue" check resolves the route id through `GetShortlistRowsAsync`: the pointer is followed without a ceiling, and the canonical row must be active, not a novel, and within the caller's ceiling. A merged stored key therefore passes when its canonical work does, so Undo on a merged entry's row restores it instead of answering 404.

The body is client-supplied, so restore runs PUT's validation on every field: note trimmed and capped at 500, `savedFromDetail` at 200, `title` at 300, `savedFrom` from the fixed set else `other`, `savedStatus` through `Enum.IsDefined` else null, `savedTotalChapters` non-negative else null, `createdAtUtc` clamped to now. Any `id` or `userId` in the body is ignored.

**Writes reach an entry stored under a merged id.** Cards and the modal only know the canonical id, so when no entry has the given id as its stored key, DELETE, PATCH and acknowledge fall back to the caller's entry whose canonical id equals it (resolved through `VisibleAsync`, at most 2000 ids), and PUT and restore treat such an entry as already existing: PUT updates its note, restore returns it unchanged. Without this, pressing the filled ribbon on a merged entry's card would answer 204, remove nothing, and the ribbon would come back on the next refetch.

Route ids are `{mangaBakaId:int}` and must be positive, else 404: `Series.MangaBakaId`, `ImportListSkip.MangaBakaId` and `SeriesLocks.ProviderIdAsync` are all `int`, so the entity's `MangaBakaId` is an `int` too and no call site needs a checked cast.

PUT and restore take `SeriesLocks.ProviderIdAsync` for the MangaBaka id around their check and insert, the lock `SeriesCreationService.CreateAsync` holds, so an entry cannot be inserted between an add's cleanup and its commit. `KeyedAsyncLock` is not reentrant: the add's cleanup (below) is a direct `db.ShortlistEntries` call made while `CreateAsync` already holds the lock, and must never go through a `ShortlistService` method that takes it again.

The list response is `{ catalogueAvailable: boolean, entries: [...] }`, one entry per visible shortlist row:

```
{ mangaBakaId, title, item: RecommendationItem | null, type: string | null, hasAnime: boolean,
  note, savedFrom, savedFromDetail, createdAtUtc,
  statusChange: { from, to } | null, newChapters: number | null,
  library: { seriesId: number | null, archived: boolean } | null }
```

`title` is the stored `ShortlistEntry.Title`, always present; the client shows it on a row whose `item` is null and uses it for aria labels and the Title sort. `type` (the dump's `type` column) and `hasAnime` (`anime_start` or `anime_end` is set) describe the canonical row and sit on the entry rather than on `RecommendationItem`, which other surfaces share; both are null and false when `item` is null.

### Hydration

`MangaBakaLocalStore.GetByIdsAsync` is not enough here: it has no `state` test and does not follow `merged_with`, unlike the detail and rail reads. A new `MangaBakaLocalStore.GetShortlistRowsAsync(ids, allowedRatings)` returns, per id, the row's `state`, its canonical id (following `merged_with` up to five hops, as `GetDetailAsync` does), status, total chapters, the canonical row's `type` and anime flag, and the same columns `GetByIdsAsync` builds a `MangaBakaRecommendation` from. It works in two steps. First it resolves canonical ids with **no** ceiling (the `FollowMergedAsync` shape), because a merged pointer row usually has no rating of its own and a ceiling on that lookup would drop every merged entry. Then it hydrates the canonical rows with the ceiling, in the same `content_rating IN` shape as `GetByIdsAsync`, so a null rating is hidden under any ceiling. It treats `type = 'novel'` like a missing row, matching `GetDetailAsync`. A null ceiling skips the filter. One query covers a full list (`MaxInlineIds` is 10,000, the cap is 2000).

Callers that only need to know which work a stored key is now use `MangaBakaLocalStore.ResolveCanonicalIdsAsync(ids)`: the same first step alone (five hops, no ceiling, no hydration), chunked by `MaxInlineIds`. An id whose chain ends on no row is absent from its answer. It serves the import's merged-key duplicate check, `RekeyAsync`, and the feedback cleanup below.

**GETs never write.** Both endpoints are read-only:

- **Merged** (canonical id differs): hydrated in memory from the canonical row, so title, status and chapters are current. The list item keeps the stored key in `mangaBakaId` (list-row writes address that; cards and the modal use the canonical id, see "Writes reach an entry stored under a merged id") and the canonical work in `item.providerId`; the ids endpoint returns both ids for such an entry, so the ribbon shows on the canonical work's card and the modal's button reads it as saved. The stored key is fixed up after each dump install, below.
- **Removed or no longer in the dump** (any `state` other than active, a novel, or no row): `item` is null. The row shows its stored `Title`, no cover and only Remove.
- **Above the ceiling**: left out by `GetShortlistRowsAsync`. **On the never-show list**: left out by `HiddenContentService.PredicateAsync`, which covers only that list and returns null when the user has none. The predicate is evaluated on the **canonical** ids: with the vector index loaded it answers `index.TryGetRow(id) && !Matches`, so a merged stored key has no row and would never be hidden. Both leave the entry stored, so raising the ceiling brings it back.
- **No dump installed**: the list answers `{ catalogueAvailable: false, entries: [] }` and the ids `{ ids: [], count: 0 }`. Without the dump a stored title cannot be checked against the ceiling or the never-show list, so nothing is sent; the entries stay stored. The client shows "Discover's catalogue is not downloaded yet" only from `catalogueAvailable === false`, never from every item being null: a list of removed rows and novels with the dump installed renders normally, each row with its stored title and Remove.

The two endpoints share one `ShortlistService.VisibleAsync`, so their filtering cannot drift.

**Re-keying merged entries** happens in one place: `ShortlistMergeService.RekeyAsync` (singleton with `IServiceScopeFactory`, like `FollowedCreatorReleaseService`).

- **When:** from `MangaBakaDumpRefreshJob`'s `if (installed)` block, inside its own try/catch with its own log line, so a database error is not reported as a failed dump refresh and does not schedule a dump retry. And once at startup from the `RunOnceAsync` block in `Program.cs`. It returns at once when `IsAvailableAsync` is false.
- **What:** an `AsNoTracking` scan of every distinct stored key with filters off, canonical ids resolved through `ResolveCanonicalIdsAsync` (chunked, so more than 10,000 distinct keys across users are all handled, and no card is hydrated). Only entries whose stored key resolves to a *different* canonical id are touched; one with no dump row is left alone.
- **How, per canonical id:** take `SeriesLocks.ProviderIdAsync(canonical)`, then **re-query under the lock** `ShortlistEntries.IgnoreQueryFilters()` for the group's stored keys plus the canonical id (a PUT may have landed since the scan), keep the oldest entry per user, move it to the canonical id when it is not already there, delete the rest, and save once. Each group is caught on its own and the pass continues; a failed group is retried on the next install or restart.

### In the library

"In the library" means what the add means. `SeriesCreationService.CreateAsync` and `SeriesRequestSubmitter` both check existence unfiltered and refuse an archived copy, so the shortlist does too: PUT's 409 and the list's `library` field read `db.Series.IgnoreQueryFilters()` by MangaBaka id, archived rows included. The list looks up the canonical id, so an entry still stored under a merged id reads as in the library once the canonical work is added, and keeps doing so after the re-key moves it, until the user removes it.

- `library: null`: not in the library. The row offers Add or Request.
- `library.seriesId` set: someone added it and the caller can see it. The row offers Open series and Remove.
- `library.seriesId: null`: it is in a root folder the caller cannot see. The row says "In the library" with only Remove, never an Add or Request that would fail.
- `library.archived: true`: the row says "Archived", with Open series when visible (restore lives on the series page).

## Server-side cleanup

- **Adding removes the entry for the person who added it.** In `SeriesCreationService.CreateAsync`, when `attributedUserId` and `series.MangaBakaId` are set, load that user's entry with `db.ShortlistEntries.IgnoreQueryFilters()` and `Remove` it, tracked, before the `SaveChangesAsync` that inserts the series. Three details matter:
  - **Outside the incognito guard.** The `UserSeriesState` block is skipped for a fully incognito series; the shortlist cleanup must not be.
  - **`IgnoreQueryFilters`.** Request approval runs under the admin's scope while the add is attributed to the requester (`SeriesRequestsController.Approve`), so a filtered query finds nothing. The receipt lookup in the same method ignores filters for the same reason.
  - **Tracked, not `ExecuteDeleteAsync`.** Only adds with a mutation id run in a transaction; an immediate delete followed by a failed insert would lose the entry.

  That covers Discover's add, the series page, import lists, Mihon and request approval. Other users' entries stay and show as in the library, see above. The lookup is by `series.MangaBakaId`, which is canonical, so an entry still stored under a merged id survives the add; it shows as in the library (the list looks up the canonical id) and is left to the user to remove.
- **Hide and thumbs down remove the entry.** `RecommendationFeedbackService.MutateAsync` deletes the caller's entry when the new state is hidden or disliked, including one stored under a key that `ResolveCanonicalIdsAsync` resolves to the feedback id (cards only know the canonical id), and `HideFranchiseAsync` does it per member through the same path, resolving the caller's keys once. Dismiss for 30 days and "read elsewhere" leave it alone. The mutation result carries `shortlistRemoved: true` when it removed an entry, and so does the franchise result when any member's went; `RecommendationFeedbackMenu`'s toast then adds that the title left the shortlist. Undoing the feedback does not restore the entry, which is why the toast says so.
- **Deleting a series does not touch shortlists.** Nothing points at the `Series` row.

## Import lists: Plan to read goes to the shortlist

Today `ImportListService.RunTrackerAsync` adds or requests every entry whose status is in the tracker's `Statuses` (default Reading and PlanToRead). From now on an entry whose remote status is `PlanToRead` is shortlisted instead, for every user, whatever their permissions. The `PlanToRead` checkbox in `ImportListsSection` stays and now reads "Plan to read (to your shortlist)", so turning it off still keeps those entries out entirely.

- **The run has no signed-in user.** The import job's scope is unrestricted, so nothing scope-bound applies: the query filter and `OwnedByScopeUser` pass everything through and `ICurrentUser.MaxContentRating` is not the list owner's. `ShortlistService` therefore has an insert overload taking an explicit user id and allowed-ratings list; it sets `UserId` on the row and counts the cap by that id. `UserInfo` gains `MaxContentRating`, read from the user row the way `FollowedCreatorReleaseService` reads it, and the run passes `ContentRating.Allowed(user.MaxContentRating)`.
- **Merged keys.** `InsertAsync` treats an entry stored under a key that now resolves to the inserted id as existing. The run resolves the user's stored keys once (`ShortlistService.MergedKeysAsync`, through `ResolveCanonicalIdsAsync`) and passes the map to every insert, so a 1,000-entry first import does not resolve the whole shortlist per candidate. A caller that passes no map gets it resolved per call.
- New `ImportListSkipReason.Shortlisted = 4` (append only). Written with the MangaBaka id when an entry is shortlisted, `SavedFrom = "importlist"`, `SavedFromDetail = tracker.Label`.
- **Where the split happens.** Right after the in-library and pending-request drop, before the `MaxPerRun` batch, the empty-batch return and the root folder check. Those return early, and a freshly provisioned SSO account has no root folders, so a split placed after them would never shortlist anything for exactly the users most likely to import a list.
- **Duplicates.** Several remote entries can fold into one `Candidate`. Its status is the strongest across `Candidate.All` (Reading or Completed beat PlanToRead), and the shortlist decision uses that, so a PlanToRead duplicate never turns a Reading entry into a shortlist. Skip rows are written per remote entry with that entry's own outcome: `Shortlisted` only on rows whose own status is PlanToRead.
- **A Shortlisted row is only "handled" while the remote status is still `PlanToRead`.** In the loop that reads `handled`, an entry whose row is `Shortlisted` and whose current status is `Reading` or `Completed` (and that status is in the prefs) goes on to `fresh` and is added or requested like any other entry. The row is then overwritten with `Added` by `RecordAsync`, and the add itself deletes the shortlist entry. So "I started reading it on AniList" still lands it in the library.
- Removing an entry from the shortlist by hand leaves the `Shortlisted` row, so the next run does not put it back.
- Shortlisting is cheap and asks nothing of the admins, so it is not capped by `MaxPerRun`. The per-user cap of 2000 still applies: at the first entry over it the run stops shortlisting, writes no row (so a later run can try once there is room), and records `ShortlistFull: true` and the count it could not take on the last-run record, which `ImportListsSection` shows as "Your shortlist is full".
- **A refused shortlist insert is recorded like a failed add.** When the insert answers "not in catalogue" (a novel, a removed row, an id the dump lacks, or a title above the user's ceiling), the entry gets an `Unmatched` row with the same `HasSeriesAsync` rule the add path uses: the MangaBaka id kept when the dump has no row (retry in a day), left null otherwise (retry in a week). An insert answering "in the library" records `Added`, like an entry the list met already owned.
- **Counting.** A `Shortlisted` row met in the `handled` loop while its entry is still PlanToRead counts as `present`, like an `Added` row. The shortlisted count is part of the result returned by every early return after the split (the empty-batch and root folder returns), so it is never lost.
- Already in the library or pending as a request: unchanged, recorded `Added`, not shortlisted.
- `ImportListLastRun` gets `Shortlisted` and `ShortlistFull`. The inbox row is raised when `added + requested + shortlisted > 0`, errors, or a manual run; `inbox.importList.finished` gets a `shortlisted` parameter, so the renderer and `InboxRendererTests` change with it.
- Entries that earlier runs already added from Plan to read stay in the library. Nothing is migrated.

`.claude/rules/import-lists.md` gets a bullet on the Shortlisted reason and the re-evaluation rule.

## Recommendations and rails

- `SeedWeightService`, `SeedSnapshot`, `SuppressedAsync` and every pool cache key ignore the shortlist. It is not a seed, not avoided, not suppressed.
- Shortlisted titles stay in every rail and in search, marked with the ribbon.
- `CustomRailService.ExclusionsAsync` (the `excludeInteracted` set used by the following rail) is unchanged: shortlisting is not feedback.
- `FollowedCreatorReleaseService` already skips titles the reader has feedback on; it also skips shortlisted ones, since the reader has plainly seen them. The job is unrestricted, so the read follows `KnownAsync`'s shape: one `IgnoreQueryFilters` query over all the user ids. That is the only place outside this feature that reads the table.
- The provenance stats in the Feedback Lab do not count shortlist entries.

## Client

### Data

`frontend/src/api/shortlist.ts`: `useIsShortlisted(id)` and `useShortlistCount()` over the one ids query (long `staleTime`), `useShortlist()`, and mutations `useSaveToShortlist`, `useUpdateShortlistNote`, `useAcknowledgeShortlistChange`, `useRemoveFromShortlist`. Saving and removing update the ids cache optimistically and roll back on error, then invalidate `shortlist`. Adding a series and the hide/dislike feedback mutations also invalidate both keys, since the server removed the entry. MSW handlers and fixtures for Storybook under `.storybook/mocks`.

### Discover tab

A fourth tab, `/discover/shortlist`, after Your Taste: `TAB_PATHS.shortlist`, `DiscoverTab` gains `'shortlist'`, label "Shortlist" with a `CountIndicator` of the entry count (hidden at 0). The route already takes `:tab?`, so `App.tsx` needs nothing. New `pages/discover/ShortlistTab.tsx`, lazy like `TasteTab`.

Layout, top to bottom (Main and Phone boards):

1. One line of `--ink-3` copy: "Titles you want to come back to. Nothing here is downloaded or counted in your taste until you add it."
2. Filter chips, inline like the catalogue filters: All, Changed since saved, Completed, Has an anime, each with its count. Client-side over the list. Sort select on the right: Recently saved (default), Oldest first, Score, Title.
3. "Changed since you saved them" `SectionHeader` and its rows, only when there are any.
4. "Waiting for you" `SectionHeader` and the rest.
5. When at least one entry is older than 180 days and the filter is All, a dashed note: "2 titles have waited more than six months." with "Show the oldest", which switches the sort to Oldest first.

Each row is a `Panel`: cover (76 by 114), title in the display face, the score and `MetaList` (type, year, status, chapters), genre chips (three), the status-change pill and "Got it" when changed, the "+N chapters" pill, the note with an edit pencil, and "Saved {relative time} from {source}" in `--ink-4`. Actions on the right, wrapping under on a phone:

- **Read ch. 1**: `PreviewChapterButton` and `SeriesPreviewReader`, unchanged. The preview writes nothing.
- **Add to library** (primary), or **Request** without `AddSeries`. Both open the existing `DiscoverDetailModal` for that item with the add rail focused, rather than duplicating root folder, monitor and incognito choices in the row. A pending request shows as a "Requested" pill instead of the button, from `useSeriesRequests`.
- **Ribbon** (filled, brand colour): removes, with an Undo toast that posts the returned entry to `restore`.
- **More menu**: Edit note, Not for me (thumbs down, which removes it), Hide this title, Open on MangaBaka.

Rows whose `library` is set follow "In the library" above instead of offering Add or Request.

The ids response's `count` drives the tab count. With no entries the tab shows the empty state: the bookmark disc, "Nothing on your shortlist", "Use the bookmark on any cover in Discover, or Shortlist on its card, to keep a title here without adding it.", a "Browse the catalogue" button, and under it a collapsed `Disclosure` (`components/ui/Disclosure`) titled "How the shortlist differs from your library" holding the comparison table above, minus the Home row.

A list/grid toggle is not in the first version. The mockup shows one; the list carries the notes and changes, which a cover grid has no room for.

### Detail modal

`DiscoverDetailModal`: the stack under `DiscoverLibraryRail` becomes a two-column row of `PreviewChapterButton` and a new `ShortlistButton` (`components/discover/ShortlistButton.tsx`), then `RecommendationFeedbackMenu` as now. The button is "Shortlist" with the outline bookmark, or, pressed, "Shortlisted" with the filled one, `aria-pressed`. Once pressed, a "Note for later" text input appears under the row and saves on blur (PATCH). The modal knows where it was opened from through a new optional `savedFrom` prop the opener passes; openers that do not pass one save as `other`. The button is hidden when the title is in the library, like the preview button.

Because `DiscoverDetailModal` is also the card for Add series search, creator pages, the series page's rails and Home's anime rail, all of those get the button for free.

### Cover cards

`RecommendationCard` and `RecommendationRow` (`components/ui/DiscoverRail.tsx`). The catalogue card's top left is the rating chip and its top right is the corner glyph (plus, or the check when owned), so the ribbon does not go top left as the mockup drew it:

- **Shortlisted and not owned:** the corner glyph becomes the filled bookmark in the brand colour instead of the plus, tooltip "On your shortlist". It is still a cue, `aria-hidden`, like the plus. The card's accessible name becomes "View {title}, on your shortlist".
- **Saving from the card:** a real `<button>` with the outline bookmark, sitting to the left of the corner glyph (top right, 36px in), shown on `:hover` and `:focus-within` under `(hover: hover)`, always present in the DOM so keyboard users reach it as the card's second stop. The poster control (`.discover-card-action`) is an absolutely positioned sibling, not a wrapper, so a second button is valid HTML; the new one comes after it in DOM order and stacks above it with `z-index`, or the overlay swallows the click. Pressed state removes. Touch devices without hover never show it; they save from the modal, where the button is large. This is the one place the card stops being a single control, which its comment (`One native control owns the whole poster`) must be updated to say.
- Owned wins over shortlisted: an owned title shows the check.

`RecommendationRow` (list view) gets the same button as a trailing `ActionIcon`, always visible.

### Copy and i18n

Every client string through `t`/`<Trans>`, then `lingui extract`. Server messages through `ILocalizer`: `error.shortlist.full`, `error.shortlist.inLibrary`, `error.shortlist.notInCatalogue` and the import list inbox parameter go into the hand-written `locales/en/server.po`, then `node scripts/i18n/sync-server-keys.mjs` (guarded by `LocalizationCatalogTests`); `lingui extract` does not see server keys. No translations: the owner does those. No em dashes.

## Tests

- `ShortlistControllerTests`: upsert is idempotent and keeps the first provenance and snapshot; note patch and clear; delete returns the entry and restore brings it back unchanged; delete of a missing row is 204; another user's rows are invisible and untouchable; 409 for a visible, an invisible and an archived library copy; cap; an above-ceiling id is a 404 on PUT; content ceiling and never-show filter both endpoints identically without deleting.
- Hydration: a merged row hydrates from the canonical row without writing, and both ids come back from `shortlist/ids`; a removed row and a novel hydrate to `item: null`.
- Merged-key writes: DELETE, PATCH and acknowledge by the canonical id reach an entry stored under the merged id; PUT by the canonical id updates it instead of inserting a second entry; restore returns it unchanged.
- An add by canonical id leaves the adder's merged-key entry in place, reading as in the library.
- `ShortlistMergeService`: re-keys a merged entry, keeps the oldest when two entries resolve to one canonical id, never trips the unique index against a PUT that lands between the scan and the lock, and continues past a failed group.
- Ceiling on a merged entry: an unrated pointer row whose canonical row is allowed stays visible; one whose canonical row is above the ceiling is hidden.
- Import list ceiling: a PlanToRead entry above the list owner's ceiling is not shortlisted, whatever the job's scope.
- Restore: every field is validated as on PUT; an existing entry is returned unchanged.
- Change detection: status change groups, chapter growth does not, acknowledge resets both.
- `SeriesCreationService`: an add deletes the attributed user's entry and leaves others'; a fully incognito add still deletes it; request approval under an admin scope deletes the requester's; a failed insert keeps the entry.
- Feedback: hide, thumbs down and franchise hide remove entries; dismiss and exposure do not.
- `ImportListServiceTests`: PlanToRead is shortlisted for both permission paths with a `Shortlisted` row and no request; a novel or above-ceiling PlanToRead entry is recorded `Unmatched`; an `AddSeries` user with no root folder still gets the shortlist; a Reading duplicate of a PlanToRead entry is added, not shortlisted; a later Reading status re-evaluates and adds; a hand-removed entry is not re-shortlisted; the cap stops the run, writes no row and sets `ShortlistFull`; a shortlist-only run raises the inbox row.
- `RecommendationService` pool key and `SeedSnapshot` are unchanged by a shortlist write (guard against someone wiring it in later).
- Storybook: `ShortlistTab` (filled, changed group, empty with the disclosure closed, no dump, requester), `ShortlistButton` both states, `RecommendationCard` shortlisted and hover. Run through the Storybook `test-run`.

## Phases

1. Entity, migration, controller, list hydration and change detection, cleanup hooks in `SeriesCreationService` and the feedback service, tests.
2. Client: hooks, Discover tab, modal button, cards, Storybook stories, extract.
3. Import lists: the Shortlisted reason, re-evaluation, counts, inbox text, settings label, rule file.

Each phase is shippable on its own; phase 3 changes existing behaviour, so the release notes call it out.
