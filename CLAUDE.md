# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Mangarr is a Sonarr/Radarr-style manga collection manager: add a series once and it monitors sites for new chapters, downloads pages, and packages them as **CBZ files with ComicInfo.xml** that Kavita parses natively. .NET 10 backend + Vite/React/Mantine SPA, EF Core + SQLite.

**See [TODO.md](TODO.md)** for the running list of things to check, fix, or add. Consult it when planning work, and add items there when you spot something out of scope for the current task.

## Commands

### Backend (.NET 10)
```bash
dotnet run --project src/Mangarr.Api      # http://localhost:8990, Swagger at /swagger
dotnet build                              # whole solution
dotnet test                               # all tests
dotnet test tests/Mangarr.Core.Tests      # one test project
dotnet test --filter "FullyQualifiedName~ChapterNumberParser"   # single test / class
```

Test projects: `Mangarr.Core.Tests`, `Mangarr.Sources.Tests`, `Mangarr.Metadata.Tests`.

### Frontend (from `frontend/`)
```bash
npm run dev      # http://localhost:5173, proxies /api + /signalr to :8990
npm run build    # tsc -b && vite build
npm run lint     # oxlint
```

### EF Core migrations
Migrations live in `src/Mangarr.Data/Migrations`; `DesignTimeDbContextFactory` supports design-time tooling. Migrations are applied automatically on startup (`db.Database.Migrate()` in `Program.cs`), so no manual step is needed to run. To add one:
```bash
dotnet ef migrations add <Name> --project src/Mangarr.Data --startup-project src/Mangarr.Api
```

## Running locally — config directory gotcha

State lives in `MANGARR_CONFIG_DIR` (SQLite DBs, logs, covers, page cache, `config.json` with the generated API key). Resolution order: `MANGARR_CONFIG_DIR` env var → `/config` if it exists (Docker) → `%APPDATA%\Mangarr` (Windows). See `AppPaths`.

**Important for dev:** point at a throwaway config dir so you don't touch the user's real library/DB:
```bash
MANGARR_CONFIG_DIR=$PWD/.devconfig dotnet run --project src/Mangarr.Api
```
`.devconfig/`, `.devlibrary/`, and `.devimport/` are gitignored dev state (the dev DB registers the latter two as root folders; `.devimport` holds folders for import testing). In `.claude/launch.json`, `preview_start backend` runs against the user's **real** `%APPDATA%\Mangarr` config and NAS library — avoid it for destructive testing; use **`backend-dev`** instead, which points `MANGARR_CONFIG_DIR` at `.devconfig` (same port 8990, so the frontend proxy works unchanged).

## Architecture

Projects (dependency direction points inward toward Core):

- **Mangarr.Api** — ASP.NET Core host. REST `/api/v1` (controllers, `X-Api-Key` auth via `ApiKeyMiddleware`), SignalR hub at `/signalr/events`, Quartz scheduled jobs, background download workers. DI wiring + all `HttpClient` rate limits live in `Program.cs`. Serves the built SPA as static files with a fallback to `index.html`.
- **Mangarr.Core** — domain layer, no infra dependencies. Entities, the `ISource` / `IMetadataProvider` abstractions, chapter-number parsing, file naming, the CBZ packaging pipeline, HTTP helpers (rate limiter, FlareSolverr), Prowlarr/qBittorrent clients.
- **Mangarr.Data** — EF Core + SQLite `DbContext` and migrations.
- **Mangarr.Sources** — site scrapers implementing `ISource` (MangaDex, MangaPill, WeebCentral, MangaFire).
- **Mangarr.Metadata** — `IMetadataProvider` implementation for MangaBaka (plus the local DB dump machinery).
- **frontend/** — React 19 + Mantine 9 + TanStack Query + react-router. API layer in `src/api/`, one page per route.

### Key extension points

- **Adding a source** = one class implementing `ISource` (in `Mangarr.Sources`) + one `AddSingleton<ISource, ...>()` in `Program.cs`. Sources are consumed as `IEnumerable<ISource>` via `SourceRegistry`. `ISource.Name` is the stable lowercase key persisted in `SourceMapping.SourceName`.
- **Page fetching contract:** `GetPagesAsync` returns `PageRequest`s that carry their own `Headers` (Referer, User-Agent, cookies) end-to-end to the downloader — never fetch a page URL without its headers, and resolve pages at **download** time not enqueue time (MangaDex at-home URLs are short-lived).

### Acquisition pipeline

Two protocols share one `DownloadQueue` (`AcquisitionProtocol` on each item):

1. **Scraper** — `DownloadQueueService` (an in-memory channel) feeds `DownloadWorkerHostedService`, which runs a fixed 2 concurrent `ChapterDownloadProcessor` workers. Each downloads pages → validates images → packages a CBZ with ComicInfo.xml atomically. On startup, in-flight scraper items are reset to `Queued` and re-signaled.
2. **Torrent** — Prowlarr search (`ProwlarrClient` / `ReleaseService`) → grab to qBittorrent (`QBittorrentClient`) → `CompletedDownloadJob` (Quartz, every minute) polls for finished downloads and imports them. Torrent items are tracked externally, so the worker recovery loop deliberately skips them.

Live progress is pushed over SignalR via `EventBroadcaster`.

### Scheduled jobs (Quartz, in `Program.cs`)

`RefreshMonitoredSeriesJob` (30 min, refreshes chapter lists + auto-queues new chapters), `MetadataRefreshJob` (24 h), `HousekeepingJob` (24 h), `CompletedDownloadJob` (1 min), `MangaBakaDumpRefreshJob` (6 h, has a stable job key so the settings endpoint can trigger it on demand).

## Non-obvious domain facts

These will bite you if unknown:

- **MangaBaka has no MangaDex IDs.** Its cross-references cover MAL / AniList / MangaUpdates / Kitsu only. Source auto-matching (`SourceMatchService`) is done by normalized-title search per source (exact or prefix match), **not** by shared UUID.
- **Local MangaBaka DB.** Mangarr keeps a local copy of the full nightly MangaBaka dump (`{ConfigDir}/mangabaka.db`, ~3 GB) served by `MangaBakaLocalStore` with API fallback (setting `mangabaka.uselocaldb`). Dump schema quirks: one flat `series` table (~130 cols, ~45% rows are `state='merged'`); `total_chapters`/`final_volume`/`merged_with` are TEXT and may be fractional ("112.5"); genres/tags/authors are JSON text; alt titles live in a `titles` JSON column; cross-refs are flat `source_*_id` columns. An FTS5 index (`mangarr_search`) is built at install time.
- **Chapter.Number is stored as REAL** (`HasConversion<double?>()`) because EF Core SQLite cannot `ORDER BY` a decimal column. Chapter identity is `(Number, Language)` with Volume as a wildcard; `ChapterSyncService.MergeDuplicates` heals duplicate rows on every refresh (sources disagree on volume info — MangaDex has volumes, scrape sites don't).
- **MangaDex delists English chapters of licensed titles** (`isUnavailable: true` in the feed). For E2E tests use unlicensed titles (e.g. Hajime no Ippo); licensed ones (Frieren) have 0 downloadable EN chapters, and some have an entirely empty EN feed.
- **MangaFire requires FlareSolverr.** `mangafire.to` sits behind an anti-bot challenge; `ChallengeAwareFetcher` (Core) caches solved cookies per host. The site is a React SPA — the source talks to its JSON API (`/api/titles`, `/api/titles/{hid}/chapters`, `/api/chapters/{id}`), and JSON fetched via FlareSolverr comes back `<pre>`-wrapped (unwrapped in `MangaFireSource`). Pages are served unscrambled; `MangaFireDescrambler` (Core) still handles any `PageRequest.ScrambleOffset > 0`.
- **ImageSharp is pinned to 3.1.12** (Split License); 4.x requires a paid build-time license key. Don't upgrade it.

## Conventions

- Backend: nullable reference types on, records for DTOs/source models, primary constructors for DI. JSON enums serialize as strings (`JsonStringEnumConverter`).
- SQLite runs in WAL mode (`PRAGMA journal_mode=WAL` on startup) with a shared cache.
- Prefer extending the existing `ISource`/`IMetadataProvider` seams over adding new infra dependencies to Core.
