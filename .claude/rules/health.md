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
