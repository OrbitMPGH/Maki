---
paths:
  - "src/Maki.Api/Services/SeriesArchiveService.cs"
  - "src/Maki.Api/Services/QueueItemCancellation.cs"
  - "src/Maki.Api/Services/DownloadQueueService.cs"
  - "src/Maki.Api/Services/SourceMappingRemovalService.cs"
  - "src/Maki.Api/Services/Upgrade*.cs"
  - "src/Maki.Api/Services/Torrent*.cs"
  - "src/Maki.Api/Services/Health*.cs"
  - "src/Maki.Api/Services/Source*.cs"
  - "src/Maki.Api/Services/ReadFileCleanupService.cs"
  - "src/Maki.Api/Services/ScrobbleService.cs"
  - "src/Maki.Api/Jobs/*.cs"
  - "src/Maki.Data/SeriesQueries.cs"
  - "tests/Maki.Api.Tests/ArchiveGuardTests.cs"
  - "tests/Maki.Api.Tests/SeriesArchiveTests.cs"
---

# Series archive

- **`Series.ArchivedAt != null` means out of the library.** An archived series stops talking to its sources and the download queue, and nothing else changes. Every new series selector, and every per-series endpoint that reaches a source or the download queue, must use `.Active()` (`SeriesQueries`) or check `ArchivedAt`, and gets a fact in `ArchiveGuardTests`. Mapping queries that start from `SourceMapping` use `m.Series!.ArchivedAt == null`. Anything that only reads the database leaves archived series alone.
- **Entry points answer 409 `error.series.archived`.** The queue backstop in `DownloadQueueService` returns the same "not enqueued" shape a disabled source does, so callers need no new branch.

## Deliberately left alone

- `MetadataRefreshJob` reads the local dump and never looks a series up at a source.
- `SeedWeightService` and recommendations: an archived series is still a rating-weighted seed.
- Stats and Rewind are about reading, not sources.
- `ImportListService` and `FollowedCreatorReleaseService` must still see the series as owned, or they would add it again.
- Scrobbling of progress and ratings keeps running. Only the plan-to-read push stops.
- `UserSeriesState.HiddenFromHomeAt` is a per-user hide, separate from archive.
- `EnqueueRepairAsync` stays open: repairing a kept file is an explicit admin fix.

## What restore relies on

- **Archive never changes `MonitorNewItems`**, so restore brings the old mode back on its own.
- **Restore goes through `RefreshMonitoredSeriesJob.RefreshSeriesAsync`, never `ChapterSyncService.SyncSeriesAsync`.** A burst of chapters released in the meantime is then bulk-held and announced instead of inserted as known.

## Queue and files

- Queue rows are cancelled through `QueueItemCancellation`, shared with `SourceMappingRemovalService`. Torrent releases are not declined.
- `removeFiles` bins with `RecycleReason.SeriesArchive`, stamps `FileRemovedAt` and leaves `Wanted` alone, so the chapters never count as missing.
