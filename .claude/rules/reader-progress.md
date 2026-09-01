---
paths:
  - "src/Maki.Api/Services/Reader*.cs"
  - "src/Maki.Api/Services/Reading*.cs"
  - "src/Maki.Api/Services/ContinueReading*.cs"
  - "src/Maki.Api/Services/*ReadCounts*.cs"
  - "src/Maki.Api/Services/Kavita*Import*.cs"
  - "src/Maki.Api/Controllers/Reader*.cs"
  - "src/Maki.Api/Controllers/ReadingProfiles*.cs"
  - "frontend/src/pages/reader/**"
---

# Reader and reading progress

Migrated out of the root CLAUDE.md so this only loads when touching reader/progress code.

- **`ReadingState` is one row per series, shared by Kavita scan and the built-in reader**, `ReadingProgressService` is its only writer — this is what makes double-counting impossible (delta vs stored mark = 0 on re-report). Duplicate rows per `SeriesId` are legal (two Kavita series can map to one local series) — never add a plain unique index on it; every index is prefixed `UserId`. Always pick via `ReadingProgressService.PickAsync`, ordered by `MaxChapter` not `UpdatedAt` (Kavita restamps `UpdatedAt` on every touch, so it flips between calls).
- **Reader settings resolve in four layers** via `ReadingProfileService.ResolveAsync` only: series override → pinned profile → profile claiming `Series.Type` → user's `reader.prefs`. A `Series.Type` is claimed by at most one profile per user (409 on conflict). `Series.Type` isn't backfilled on upgrade — lands null, filled by metadata refresh.
- **Reader page order is `CbzReader.PageNames`** (extension filter + `OrderBy(FullName, OrdinalIgnoreCase)`); `VolumeChapterScanner.ScanCbzBoundaries` maps chapter markers onto that same list — drift between them opens chapters at the wrong page. When markers don't name a chapter, or name it more than once, the reader serves the **whole archive** rather than guess a range. `ReaderArchiveCache` is a bounded LRU singleton; its `Invalidate` must be called on delete (SQLite reuses rowids) or a later adopt inherits a stale page list.
- **`ChapterProgress` is ground truth for read state; `MaxChapter` is not.** Read counts = completed `ChapterProgress` rows for downloaded chapters (list and detail endpoints must use the identical condition). `MaxChapter` survives only as an internal aggregate for Rewind deltas / forward-only tracker pushes. Marking unread leaves an `UnreadAt` tombstone rather than deleting the row (a delete gets recreated by the next Kavita tick).
- **`ChapterProgress.Watched` is "ticked off, not read"** ("I've seen the anime"). Always carried alongside `Completed`, so the UI stops calling those chapters unread, but it emits **no `StatsEvent`** — this is the same trade the Kavita read import makes, for the same reason (dating a whole back catalogue to one day wrecks Rewind). `ReadCounts.Read` counts it and `ReadCounts.ReadFor` does **not**: the first is the UI count, the second feeds progression (`UserMetricsService.FullyReadAsync`) and must not hand out "fully read" for a season nobody read. Anything new counting reads has to pick a side deliberately. `MarkWatchedAsync` still raises `MaxChapter`, silently, via `ImportSilentAsync` — skip that and the first genuine read after a watched season emits a delta of the whole season — which does mean the scrobbler pushes that progress to AniList/MAL, again exactly as a Kavita import does. Reading a watched chapter clears the flag and becomes a real read, which is why `SaveProgressCoreAsync`'s transition gate is `row is { Completed: true, Watched: false }` and not `row.Completed` — the sticky flag is already set, so keying off it alone would swallow the read as a re-read.
- **Three progress fields, don't conflate**: `PageIndex` (resume position, may move backward), `Completed` (sticky, idempotency token), `ReadingState.MaxChapter` (forward-only high-water mark). Marking unread clears the first two, never lowers the third. One-shots (`Number == null`) must not touch `MaxChapter` — no number to raise it to.
- **Reading time is measured client-side and reported as deltas** (`useReadingClock`, tab visible+focused+not idle). `flushProgress` always sets `Final` on hide/unmount/chapter-change to flush banked time regardless of threshold. `ReaderService` clamps a single report to 15 min. Native reader only — OPDS page fetches and `MarkRead` pass `TimeReport.None` (a page request isn't proof anyone looked at it).
- **Kavita read-status import is invisible to Rewind on purpose** (`KavitaReadImportService` → `ReadingProgressService.ImportSilentAsync`) — Kavita doesn't say *when* chapters were read, so dating them today would dump the whole back catalogue onto one day. Imported rows carry `PageCount = 0`; `Completed AND PageCount = 0` is how later code identifies an import.
- **`ContinueReadingService.NextForAsync` is the only "what's next" resolver** — reads `ChapterProgress` only, never `ReadingState` (would double-count/multiply on duplicate rows).
