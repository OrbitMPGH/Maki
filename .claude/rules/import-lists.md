---
paths:
  - "src/Maki.Api/Services/ImportList*.cs"
  - "src/Maki.Api/Controllers/ImportLists*.cs"
  - "src/Maki.Api/Jobs/ImportListJob.cs"
  - "src/Maki.Core/Configuration/ImportListPrefs.cs"
  - "src/Maki.Core/Entities/ImportListSkip.cs"
  - "src/Maki.Core/Scrobbling/*Tracker.cs"
  - "frontend/src/components/ImportListsSection.tsx"
---

# Import lists

Sonarr-style: per user, pull the AniList/MAL/Kitsu/MangaBaka list on a schedule, map to MangaBaka ids, add missing series monitored.

- **`ImportListSkip` is provenance, not only skips.** `Reason` is persisted (append only): `Unmatched` (no MangaBaka id, retried after 7 days or on manual Retry), `Removed` (series deleted, never re-added), `Ignored` (user said never), `Added` (this run added it, or it was already present when seen). `SeriesController.Delete` flips every `Added` row for that MangaBaka id to `Removed` across users, which is the only thing stopping a deleted series from coming back on the next tick. A series the user added by hand and later deleted has no row and will come back if it sits on their list.
- **Permission split lives in the service, not the controller.** `AddSeries` holders go through `SeriesCreationService.CreateAsync` with `addedFrom: "importlist"` and a stable `clientMutationId` from (user, service, remoteId). Everyone else goes through `SeriesRequestSubmitter` (extracted from `SeriesRequestsController.Create`, keep both callers on it), still capped at `MaxPerRun` even on a full sync so one click cannot file hundreds of admin requests.
- **`POST importlists/run` has two shapes.** `full: false` runs inline and returns the result. `full: true` starts a background run, returns 202 `{ started: true }`, and reports through the `ImportListFinished` inbox row and the `importlist.lastrun.{service}` user setting. A per-user lock answers 409 while either runs; the scheduled tick silently skips a locked user.
- **Dump unavailable is not an error.** Entries without a MangaBaka id are skipped for that tick with `dumpUnavailable: true` on the last-run record and no inbox row, so a fresh install does not get a warning every interval until the dump downloads.
- **`ListAsync` pages until the API says stop and also until a page adds nothing**, because MangaBaka v2 is beta and Kitsu can hand back a `next` link with an empty page. MangaBaka and MAL run one pass per status since neither documents repeated status params as OR.
- **Root folder for a non-admin is chosen server-side** (first visible `UserRootFolder`) because `RootFolderController` is admin-only; the frontend hides the select when it has nothing to list.
- Settings: instance `importlist.enabled` / `importlist.interval` (min 15) under `settings/importlists`; per user `importlist.prefs` JSON blob (in `UserSettingKeys.Fixed`).
