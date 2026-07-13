# CLAUDE.md

See [README.md](README.md) for overview, [TODO.md](TODO.md) for backlog.

## Directory ownership

- `Mangarr.Core` — domain, no infra deps. Entities, `ISource`/`IMetadataProvider`, parsing, CBZ pipeline.
- `Mangarr.Data` — EF Core + SQLite, migrations.
- `Mangarr.Sources` — one `ISource` impl per site.
- `Mangarr.Metadata` — MangaBaka provider + local dump + embeddings.
- `Mangarr.Api` — host, controllers, Quartz jobs, DI wiring, download workers.
- `frontend` — Vite/React/Mantine SPA.

## Build/run gotchas

- API: `:8990`. Frontend dev server: `:5173`, proxies `/api` + `/signalr` to `:8990`.
- EF migrations auto-apply on startup — no manual step. To add one: `dotnet ef migrations add <Name> --project src/Mangarr.Data --startup-project src/Mangarr.Api`.
- State (SQLite, logs, covers, page cache, `config.json`/API key) resolves via `MANGARR_CONFIG_DIR` → `/config` (Docker) → `%APPDATA%\Mangarr` (Windows). In dev, set `MANGARR_CONFIG_DIR=$PWD/.devconfig` so you don't touch the real library/DB. `.claude/launch.json`'s `backend` preset uses the **real** APPDATA config — use `backend-dev` instead for anything destructive.

## Non-obvious domain facts

- **MangaBaka has no MangaDex IDs** — cross-refs cover MAL/AniList/MangaUpdates/Kitsu only. `SourceMatchService` matches by normalized-title search per source, not shared UUID.
- **Local MangaBaka DB** (`{ConfigDir}/mangabaka.db`, ~3 GB, `MangaBakaLocalStore`, API fallback via `mangabaka.uselocaldb`). One flat `series` table (~130 cols, ~45% `state='merged'`); `total_chapters`/`final_volume`/`merged_with` are TEXT and may be fractional; genres/tags/authors/alt-titles are JSON text; cross-refs are flat `source_*_id` columns; FTS5 index `mangarr_search` built at install time.
- **Chapter.Number is stored as REAL** — EF Core SQLite can't `ORDER BY` decimal. Identity is `(Number, Language)` with Volume as wildcard; `ChapterSyncService.MergeDuplicates` heals dupes on every refresh (sources disagree on volume info).
- **MangaDex delists English chapters of licensed titles** (`isUnavailable: true`). For E2E tests use unlicensed titles (e.g. Hajime no Ippo) — licensed ones (Frieren) have 0 downloadable EN chapters.
- **MangaFire requires FlareSolverr.** `ChallengeAwareFetcher` caches solved cookies per host. Talks to the site's JSON API; FlareSolverr-fetched JSON comes back `<pre>`-wrapped (unwrapped in `MangaFireSource`).
- **ImageSharp pinned to 3.1.12** (Split License) — 4.x needs a paid build-time license key. Don't upgrade it.
- **Page fetching contract:** `GetPagesAsync` returns `PageRequest`s carrying their own `Headers` end-to-end to the downloader — never fetch without them, and resolve pages at **download** time not enqueue time (MangaDex at-home URLs are short-lived).
- **Two acquisition protocols** share one `DownloadQueue` (`AcquisitionProtocol` on each item): scraper downloads run through `DownloadWorkerHostedService` (2 fixed concurrent workers); torrent releases go through Prowlarr search (`ReleaseService`) → qBittorrent grab → `CompletedDownloadJob` (polls, imports finished downloads). Startup worker-recovery only resets scraper items — torrent items are tracked externally.
- **Adding a source** = one `ISource` impl in `Mangarr.Sources` + one `AddSingleton<ISource, ...>()` in `Program.cs`.
