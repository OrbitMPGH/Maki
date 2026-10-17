---
paths:
  - "src/Maki.Api/Services/ImportList*.cs"
  - "src/Maki.Api/Controllers/ImportLists*.cs"
  - "src/Maki.Api/Jobs/ImportListJob.cs"
  - "src/Maki.Core/Configuration/ImportListPrefs.cs"
  - "src/Maki.Core/Entities/ImportListSkip.cs"
  - "src/Maki.Core/Scrobbling/*Tracker.cs"
  - "frontend/src/components/ImportListsSection.tsx"
  - "src/Maki.Api/Services/SeriesRequestSubmitter.cs"
  - "tests/Maki.Api.Tests/ImportList*.cs"
---

# Import lists

Sonarr-style: per user, pull the AniList/MAL/Kitsu/MangaBaka list on a schedule, map to MangaBaka ids, add missing series monitored.

- **`ImportListSkip` is provenance, not only skips** (`Reason` values are documented on `ImportListSkipReason`, append only). A run records every list entry already in the library (or pending as a request) as `Added`, and `SeriesController.Delete` flips every `Added` row for that MangaBaka id to `Removed` across users. That flip is the only thing stopping a deleted series from coming back on the next tick, so a series no run saw while it was in the library has no row and will come back if it is on the list.
- **Permission split lives in the service, not the controller.** `AddSeries` holders go through `SeriesCreationService.CreateAsync` with `addedFrom: "importlist"` and a stable `clientMutationId` from (user, service, remoteId). Everyone else goes through `SeriesRequestSubmitter` (extracted from `SeriesRequestsController.Create`, keep both callers on it), still capped at `MaxPerRun` even on a full sync so one click cannot file hundreds of admin requests.
- **`POST importlists/run` always runs in the background.** Both `full: false` and `full: true` go through `ImportListService.StartRun`, return 202 `{ started: true }`, and report through the `ImportListFinished` inbox row (always raised for a manual run) and the `importlist.lastrun.{service}` user setting. The run uses `CancellationToken.None`, never `RequestAborted`. A per-user lock answers 409 while either runs; the scheduled tick silently skips a locked user.
- **Dump unavailable is not an error.** Entries without a MangaBaka id are skipped for that tick with `dumpUnavailable: true` on the last-run record and no inbox row, so a fresh install does not get a warning every interval until the dump downloads.
- **`ListAsync` stops on an empty page as well as on the API's own end marker**, because Kitsu can hand back a `next` link with an empty page; MangaBaka (v2 is beta) also stops on a page that adds no new id. MangaBaka and MAL run one pass per status since neither documents repeated status params as OR.
- **MangaUpdates ids are base36 slugs** (lowercase, what `Series.MangaUpdatesId` and the dump store), converted to int64 `series_id` only at the HTTP boundary in `MangaUpdatesTracker`. The tracker owns its per-user 5.5 s gate between list writes (ratings bypass it) and retries a 412 once, so `ScrobbleService.PaceFor` stays the default. Saving different MangaUpdates credentials in Settings deletes the stored session token and calls `MangaUpdatesTracker.Forget`. Entries on custom MangaUpdates lists read as `Other` and are not imported. The dump lookup is `GetIdsByMangaUpdatesIdsAsync`, parameterized and case-insensitive (a couple of dump rows are mixed case), served by the `lower(...)` expression index `ix_ext_mangaupdates`.
- **Root folder is chosen server-side** by `ImportListService.RootFolderForAsync`: the saved `prefs.RootFolderId` if the user can still see it, otherwise their first visible folder (all folders for `AllRootFolders` users). The frontend hides the select when it has nothing to list.
- Settings: instance `importlist.enabled` / `importlist.interval` (min 15) under `settings/importlists`; per user `importlist.prefs` JSON blob (in `UserSettingKeys.Fixed`).
