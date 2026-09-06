---
paths:
  - "src/Maki.Api/Services/Health*.cs"
  - "src/Maki.Api/Controllers/Health*.cs"
  - "src/Maki.Api/Jobs/Health*.cs"
  - "src/Maki.Core/Entities/Health*.cs"
  - "src/Maki.Core/Reading/ArchiveHealth*.cs"
  - "frontend/src/api/health.ts"
  - "frontend/src/pages/HealthPage.tsx"
---
# Health and reviewed file management

- All health endpoints and archive previews require Admin. Health data is instance-wide and includes filesystem paths.
- Scans are read-only. Missing/unreadable roots are partial scans, never proof that prior findings resolved. Unsupported image decoding is partial analysis, not corruption.
- ChapterFile.ReleaseHash is acquisition provenance, not a content hash. HealthFile.ContentHash is SHA-256. Ignore decisions belong to a content version.
- Repair downloads have HealthOperationId and HealthRepair origin. They stage candidates and never execute ordinary import, source fallback, retry, statistics, or reader-notification paths.
- Replacements require every chapter linked to a shared archive. Keep chapter IDs, Wanted, Completed, Watched, and history. Page layout changes require explicit approval to reset bookmarks/resume across users.
- File deletion must not call ChapterController.Delete. Preserve chapter records and Wanted flags. The durable deleting state recovers missing file links after a crash; a failed filesystem delete retains links.
- Recover applying/deleting journals before serving the library. Completed repair rollback files are temporary recovery material, not quarantine. Never follow links/junctions or accept arbitrary paths in health APIs.
- Unlinked findings carry a match (`HealthMatchService`): owner series by folder containment, chapter by `ReleaseNameParser`, counterpart = the ChapterFile already holding that chapter. Read-only inference; a wrong guess must only produce a bad suggestion. No counterpart means the chapter is free and the archive can be imported directly.
- `POST health/imports` is the user asking for the ordinary import and runs `CbzLinkService.RescanSeriesAsync` per owning series, then queues a scan of the selection so unlinked findings resolve. Repair candidates remain barred from every import path.
- Bulk actions cap at 500 files. `POST health/findings/review` skips findings whose version is no longer the file's current version, so a bulk sweep cannot launder an ignore onto new bytes.
- Repetition evidence is grouped (`PageGroup`), never pairwise: N repeats of one page are one group, not N(N-1)/2 pairs. Blank pages are a single counted group and are never compared against each other; a chapter break, an insert and an end card all read as blank. Bump `ArchiveHealthAnalyzer.Version` when the analysis shape or heuristics change - it keys the `HealthAnalyses` cache and forces re-analysis - and keep `HealthScanService.Analysis` tolerant of lists an older version never wrote.
- Page count is not comparable across long-strip releases; sites slice the same chapter differently. Stacked pixel height is (`MatchCounterpart.PixelHeight`).
- `POST health/deletions/bulk` is one confirmation for the batch, capped at 100. Every file still goes through `PreviewDeleteAsync` + `ApplyAsync`, so a file whose bytes changed is refused and reported instead of failing the batch.
