# TODO

A running list of things to check, fix, or add. Add items freely — newest at the top of each section. When something is done, delete it. After each completed item, push and commit the changes. Keep the priority headings in place even if they are empty.

## To do

### High Priority

- **Global per-source priority preference.** Settings-level ordered list of sources (e.g. "prefer MangaDex > MangaFire > MangaPill") applied automatically whenever a series auto-matches multiple sources, instead of every mapping defaulting to `Priority = 1` and leaving the user to hand-edit per series. Pairs with the `Priority = 1` tie bug noted under Known Issues — this is the real fix, not just staggering numbers.

- **Automatic retry of failed downloads.** Confirmed gap: a `Failed` queue item only retries via a manual click (`QueueController` `/retry`) or a manual "Search missing" bulk action — no scheduled sweep retries it. `DownloadQueueItem.RetryCount` is already tracked but nothing acts on it. Add a job that periodically re-enqueues `Failed` items with an escalating backoff (and a retry cap so a permanently-dead source doesn't loop forever); surface retry count / next-attempt on the Activity page.

- **Update-available notice / in-app updater.** Poll GitHub releases for a newer tag, show a "new version available" banner with the changelog. Docker installs get a notify-only prompt (pull manually); a bare install can offer self-update. Feed the "update available" signal into the Notifications subsystem.

### Medium Priorty

- **Import Lists.** Periodically sync an external list into the library and auto-add new titles — MyAnimeList and AniList "Planning"/"Reading"/"Currently Reading" lists to start. Reuse the scrobble OAuth already held for both providers. Per-list: monitor mode applied to added series, root folder, refresh interval; a preview of what would be added. Match imported entries to MangaBaka via the same normalized-title path as `SourceMatchService`.

- **Calendar / upcoming releases + iCal feed.** A calendar view of expected next chapters per monitored series (estimate cadence from chapter release history), plus a subscribable read-only `.ics` feed (token-authed URL) so external calendar apps can follow it. \*arr calendar parity.

- **Health checks / System Status page.** `SystemController` already exposes `/system/health` (source-mapping errors, missing root folders, monitored-but-unmapped series, stale/missing MangaBaka dump) and `/system/status` — but there's **no frontend Status page** and the check set is thin. Work: build the Status page UI, and broaden checks — FlareSolverr down/unconfigured, root folder unwritable (not just missing), disk space low, embeddings index unbuilt, pending/failed migration warnings, qBittorrent/Prowlarr/Kavita connectivity. Feed health transitions into the Notifications subsystem.

- **OPDS server.** Serve the library over OPDS (with page-streaming) so reading apps — Panels, Chunky, Mihon/Tachiyomi, KOReader — connect straight to Mangarr without a Kavita hop. Token-authed feed URL; some overlap with Kavita's own OPDS, so gate behind a setting.

- **Series metadata & cover overrides.** Per-series manual edit: override title, cover art (pick from source candidates or upload a file), tags, and default monitor mode. Overridden fields lock so a metadata refresh doesn't overwrite them.

- **Library tags + saved smart filters.** `Series.Tags` (List&lt;string&gt;) **already exists as a field but is never surfaced** — no assignment UI, no filtering. Work: tag assignment on series, tag filter + saved filter views on the Library page (e.g. "ongoing, behind, action"), bulk actions over a filtered set, and a tag target for Import Lists. Grows in value as the library gets big.

- **Global search / command palette.** No cross-page search today — `LibraryPage`'s filter box is local to that page only. Add a header-level `Ctrl+K` palette to jump straight to any series (by title) from any page (Add Series, Settings, Activity, etc.). Scales in value with library size.

- **Move series to a different root folder.** `Series.RootFolderId` is only set at creation (`SeriesController` series-add) — no endpoint or UI to relocate an existing series' on-disk files to a different root folder later. Reorganizing a library across mounts currently means manual DB + filesystem edits. Work: move the series folder, update `RootFolderId` and any `ChapterFile.RelativePath` assumptions, re-trigger a Kavita scan of both the old and new locations.

- **Related series on series page.** Show related series directly on a series page. For easy adding. (Spinoff, etc.)

- **Add information about the series potential anime.** If a series has an anime, add info about it on the "add" page.

### Low Priority

- **Scrobble sync history: pagination + filter.** `/scrobble/status` caps recent activity at the last 40 rows with no pagination, no filter by series/service/date. Fine at small scale; extend once sync volume grows.

### Road to 1.0.0

Currently versioned in the 0.x line (see `Directory.Build.props` + git tags) — 0.x keeps the
freedom to break the API/DB schema while the following gates close. Promote to 1.0.0 only after a
full release cycle passes with no data-loss or migration bug and a real upgrade-with-data works.

- **Upgrade-with-real-data test** — cut a 0.9.x, then upgrade a real install with real data across
  it. Highest-value test currently missing; the first released-to-released migration must not run
  first on a stranger's library.
- **Clean-machine install from the ghcr image only** — no local build, no dev config, README
  followed verbatim.
- **Controller smoke tests over `/api/v1`** before freezing the surface — domain coverage is
  good, the API layer is the hole (`Mangarr.Api.Tests` is one file).
- **Soak the rate-limit / cooldown work** — let it run a week of real downloads before freezing.
- **Get it in front of real users** (r/selfhosted, \*arr Discord) — each finds something you can't.

## Known issues / to investigate

- **Auto-matched (and manually-added) source mappings all default to `Priority = 1`** (`SourceMatchService.AutoMatchAsync`, `SourceMappingController` create). When a series has 2+ enabled mappings, `DownloadQueueService.EnqueueChapterAsync`'s `.OrderBy(m => m.Priority).FirstOrDefault()` breaks the tie on whatever order EF returns rows in — not a real preference. Effectively random best-source pick until the user manually edits priorities in Settings. Fix: stagger priority on auto-match (1, 2, 3…) in a stable order (e.g. source registration order).
- Add Series' per-result "Add" modal and Discover's filter/detail modals can't be visually verified in the headless preview (empty modal shell, screenshots time out) — a Mantine + preview-pane rendering limitation, not an app bug. Drive these via DOM reads/events instead of screenshots.

## Answered

- **Settings secrets are not encrypted at rest** — verified against a live DB: `qbittorrent.password`, `kavita.apikey`, `prowlarr.apikey`, `scrobble.malclientsecret` and tracker tokens are stored as plaintext in `AppConfig`. Accepted as a deliberate trade-off (matches Sonarr/Radarr/Prowlarr; any key would live beside the DB anyway). Documented in [CLAUDE.md](CLAUDE.md) — the real boundary is filesystem permissions on the config dir.
- **Library filter textbox clears fine** — earlier "select-all + delete didn't clear" was a browser-automation artifact. Verified: typing filters 36 → 1 card, clearing restores all 36.
- **`/series/59` "Monitor: none" combobox opens and works** — earlier "no visible listbox" was the same preview-pane rendering limitation. Verified: dropdown reports `aria-expanded=true` / visible with all 3 options, and selecting one fired the mutation ("Monitoring 105/123 chapter(s)").
- **Add Series reusing `/recommendations/detail|reviews`** is intentional — `AddSeriesPage` renders the shared `DiscoverDetailModal` and pads a search result into a `RecommendationItem` (see the comment above `toRecommendationItem`).
