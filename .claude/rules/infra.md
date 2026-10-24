---
paths:
  - "src/Maki.Api/Logging/**"
  - "src/Maki.Api/Services/*Backup*.cs"
  - "src/Maki.Api/Services/*Settings*.cs"
  - "src/Maki.Api/Services/MemoryDiagnostics.cs"
  - "src/Maki.Api/Jobs/*Idle*.cs"
  - "src/Maki.Api/Jobs/Artifact*.cs"
  - "src/Maki.Core/Io/**"
  - "Dockerfile"
  - "distribution/docker/Dockerfile"
  - ".github/workflows/*.yml"
  - "src/Maki.Api/Services/KavitaUser*.cs"
  - "src/Maki.Api/Program.cs"
  - "src/Maki.Data/Migrations/**"
  - "src/Maki.Data/Identity/**"
  - "src/Maki.Core/Http/**"
  - "src/Maki.Api/Configuration/**"
  - "src/Maki.Data/DataScope.cs"
  - "src/Maki.Api/Jobs/DiscoverCacheWarmJob.cs"
  - "src/Maki.Api/Services/UserScopedQuery.cs"
---

# Infra: logging, backup, scoping, settings, migration

- **Multi-user migration inserts a placeholder admin at `Id = 1`; that id is part of the schema.** Migrations can't compute a password hash (PBKDF2 needs `UserManager`), so it inserts `PasswordHash = null, PendingSetup = 1`. Only `POST auth/setup` may claim it, only while the flag is set; `auth/login` refuses such a row. This is how an existing single-user install upgrades losslessly (its library is already user 1). `MultiUserMigrationTests` runs real `Migrate()` over a populated pre-upgrade DB — `TestDb` (`EnsureCreated()`) can't cover this.
- **Reading/preferences/trackers are per-user; the library is not.** `StatsEvent.UserId` is nullable on purpose: null = library event (visible to everyone), non-null = someone's reading (visible only to them).
- **Scoping is structural**: `DataScope` + EF global query filters on every user-owned table (`UserId == scope.UserId` unless `Unrestricted`), plus a root-folder EXISTS filter on `Series` and matching filters on `Chapter`/`ChapterFile`/`SourceMapping`/`DownloadQueueItem` (skipping these leaves the child table readable by id regardless of series visibility). `DataScope` defaults to **unrestricted**, not deny-all; don't flip it (see the class doc). Background jobs filter by user explicitly instead. `OpdsAccessService` is the one place that must bypass the filter (`IgnoreQueryFilters()` + explicit `UserId ==`) since an OPDS request starts with scope narrowed to nobody.
- **Kavita is bound to exactly one Maki user** (`kavita.userid`, `KavitaUserResolver`, defaults to lowest-numbered enabled admin). All Kavita sync paths act as that user only; `reader.pushtokavita` pushes only for them.
- **Per-user vs instance settings** (`UserSettingKeys`): instance = deployment/shared app registration; user = personal preference. Two scrobble keys stay instance-wide despite describing per-user data because they describe the *job* (`scrobble.lastsyncat`, `scrobble.libraryids`) and Kavita is single-user anyway. A spec with nothing set **deletes the row** rather than storing `{}` — "no default" and "default that constrains nothing" are the same state. Read per-user keys through `IUserSettings` (or `IUserSettingsStore` for background paths) — never `SettingsService`, which opens a fresh DbContext per key.
- **Logging is configured in one place (`Maki.Api/Logging/`), and the interesting half is what it *drops*.** Everything logs through `ILogger<T>`; `MakiLogging` owns levels, sinks, the category floors (`System.Net.Http.HttpClient` at Warning, `Microsoft.EntityFrameworkCore`, `Quartz`) and `IsKnownBenign`. Don't remove the floors: `OutboundHttpLoggingHandler` (registered via `ConfigureAll<HttpClientFactoryOptions>`, last so it sits innermost and sees each retry) replaces the framework's per-call lines. `HostStartupTests` boots an empty config dir and fails on any ERR or WRN line. Inbound, `HttpRequestLogPolicy` tiers by what a request *did*: exceptions and 5xx at Error, other 4xx (not 401/403/404/499) and anything over `SlowRequestMs` at Warning, writes at Information, successful reads at Debug, and images/assets/hub traffic below that. The read/write split is deliberately not a list of polling endpoints, which would rot. The `config.json` logging keys are read once at startup because the sinks are built before the host exists.
- **The logger is up before `config.json` is read** (`MakiLogging.Bootstrap`, so `RestoreBootstrap` and the pre-migration backup are logged). `MakiLogging.Configure` disposes *the bootstrap logger specifically*, never `Log.CloseAndFlush()`, because `WebApplicationFactory` boots the host more than once per process. `ConfigFileProvider` rewrites config.json whenever a release adds a key the file predates, since these settings have no UI.
- **Settings secrets are stored in plaintext** in `AppConfig` (deliberate, same as Sonarr/Radarr/Prowlarr — an encryption key would just live beside the DB anyway). Treat `{ConfigDir}`, backups, and any copy of `maki.db` as credential material.
- **Rate limits are shared state.** Any 429/503 becomes `RateLimitException`, feeding one `DownloadQueueService` cooldown. Exception: `challenge-fetcher` only converts 429 — Cloudflare serves its challenge as 503, which `ChallengeAwareFetcher` needs intact to hand off to FlareSolverr. Retries skip 429/503 and only cover GET/HEAD (so a qBittorrent add is never replayed).
- **Backup/restore**: backup = consistent `maki.db` snapshot (SQLite online-backup API, never `File.Copy` a live WAL db) + `config.json` + `manifest.json`; exclusions are listed on `BackupService`. `Program.cs` auto-backs-up **before** `Database.Migrate()` (migrations are forward-only), except on a fresh database with no applied migrations, where `BackupService` would query tables that do not exist yet. Restore is staged into `{ConfigDir}/restore-pending`, applied at next startup, then the app self-exits for a supervisor to restart it. A downgrade guard rejects a backup from a newer schema.

- **Read `GET api/v1/system/memory` (admin, `MemoryDiagnostics`), never a single RSS figure.** It separates managed heap from native (ONNX weights, SQLite and Playwright never appear in `GC.GetTotalMemory`), live from collectable (`?collect=true` forces a compacting collection, stalling requests, hence opt-in), and anonymous memory from page cache. **What Unraid and most dashboards show is the cgroup's `memory.current`, which counts page cache**; without a container memory limit the kernel never asks for those pages back.

- **Only one heavy artifact build runs at a time** (`ArtifactBuildGate`, taken by `MangaBakaDumpRefreshJob`, `PrebuiltIndexJob`, `RecoGraphJob`, `CoReadJob`, `TasteVectorJob`, `ReaderCohortJob` and `DiscoverCacheWarmJob`), so startup peak memory is the largest build rather than the sum. **Not a Quartz `MaxConcurrency` setting**, which would throttle the download workers and the completed-download poll too; the pool is raised to 20 instead. Lease disposal and the compacting collect on the last release are documented on the gate. `DiscoverCacheWarmJob` builds the vector and catalogue indexes on any non-scheduled trigger (a dump install); on its first scheduled fire after startup it builds **only when the dump, vector database or taste file changed since the last warm** (`discover.warmedstamp`, saved after every successful run), so the first search after a plain restart builds them itself (~9 s). Later scheduled fires only refresh an index that is loaded but stale (`IsCurrent`).

- **A scan that reads a file end to end drops it from the page cache afterwards** (`PageCache.DropAfterScan`, `posix_fadvise(POSIX_FADV_DONTNEED)`, Linux only, `MAKI_DROP_SCAN_CACHE=0` disables). Four callers: a health **verify** (not a plain analysis), the catalogue index build, the vector index build, and `DiscoverService` after a batch of rails, which is safe only because the rails cache for twelve hours. That drop is **deferred** (`ScheduleScanCacheDrop`, two-minute quiet period, re-armed by each batch) so genre rails built in the same page view don't hit a cold file. Not a leak fix: the pages are clean, but dashboards count them and they evict other containers' cache. Do **not** extend it to point queries: their pages are worth keeping.

- **The Docker image sets `DOTNET_EnableWriteXorExecute=0`** to drop the JIT's double mapping (both views count toward RSS); the trade-off is in the Dockerfile comment and `-e DOTNET_EnableWriteXorExecute=1` takes the hardening back. The real saving is about 29 MB of working set, because the code pages move from `RssShmem` to `RssAnon`: quote the working-set difference, never the shmem column on its own.

- **The image caps glibc to two malloc arenas (`MALLOC_ARENA_MAX=2`)**, or native heap grows with the host's core count (reason in the Dockerfile).
- **The first monitored-series sync starts at +20 minutes, not +5**: it launches a headless browser (MangaFire), and startup job timings are a memory budget. Keep the browser-launching sync out of the artifact build window.
