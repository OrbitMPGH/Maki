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
- **Local MangaBaka DB** (`{ConfigDir}/mangabaka.db`, ~3 GB, `MangaBakaLocalStore`, API fallback via `mangabaka.uselocaldb`). One flat `series` table (~130 cols, ~45% `state='merged'`); `total_chapters`/`final_volume`/`merged_with` are TEXT and may be fractional; genres/tags/authors/alt-titles are JSON text; `tags_v2` is richer — per-tag categorical weight (`core`/`defining`/`recurrent`/`incidental`/`unweighted`), `is_spoiler`, `series_count` (IDF), 96% coverage vs 50% for plain `tags` (and `genres_v2` is empty — use `genres`); cross-refs are flat `source_*_id` columns; FTS5 index `mangarr_search` built at install time.
- **Chapter.Number is stored as REAL** — EF Core SQLite can't `ORDER BY` decimal. Identity is `(Number, Language)` with Volume as wildcard; `ChapterSyncService.MergeDuplicates` heals dupes on every refresh (sources disagree on volume info).
- **Monitoring is only `Series.MonitorNewItems`** — there is no series-level `Monitored` flag (dropped in `RemoveSeriesMonitoredFlag`). It was write-once at Add and nothing ever updated it, so "Monitor: none" left every library card still showing the eye. `SeriesDto.Monitored` is now derived (`MonitorNewItems != None`) and can't drift. An unmonitored *add* is just mode `None`.
- **`RefreshMonitoredSeriesJob` skips only Completed series that already hold every chapter MangaBaka lists** — compared against the **highest chapter number**, never the count: sources carry specials/one-shots MangaBaka doesn't count, so a count reads "ahead" (244 vs 240) on a series that's exactly in step. Don't gate refresh on "behind MangaBaka's total" alone — its total lags the sources on active titles (real data: 195 chapters held against a reported 187), so that would stall the ongoing series until MangaBaka caught up.
- **MangaDex delists English chapters of licensed titles** (`isUnavailable: true`). For E2E tests use unlicensed titles (e.g. Hajime no Ippo) — licensed ones (Frieren) have 0 downloadable EN chapters.
- **MangaFire requires FlareSolverr.** `ChallengeAwareFetcher` caches solved cookies per host. Talks to the site's JSON API; FlareSolverr-fetched JSON comes back `<pre>`-wrapped (unwrapped in `MangaFireSource`).
- **ImageSharp pinned to 3.1.12** (Split License) — 4.x needs a paid build-time license key. Don't upgrade it.
- **Page fetching contract:** `GetPagesAsync` returns `PageRequest`s carrying their own `Headers` end-to-end to the downloader — never fetch without them, and resolve pages at **download** time not enqueue time (MangaDex at-home URLs are short-lived).
- **Two acquisition protocols** share one `DownloadQueue` (`AcquisitionProtocol` on each item): scraper downloads run through `DownloadWorkerHostedService` (worker count from `download.concurrentchapters`, default 2, read once at startup); torrent releases go through Prowlarr search (`ReleaseService`) → qBittorrent grab → `CompletedDownloadJob` (polls, imports finished downloads). Startup worker-recovery only resets scraper items — torrent items are tracked externally.
- **Settings secrets are stored in plaintext** in the `AppConfig` table — qBittorrent password, Kavita/Prowlarr API keys, MAL client secret, tracker OAuth tokens. This is deliberate (same as Sonarr/Radarr/Prowlarr): an encryption key would have to live beside the DB in the config dir, so it would stop shoulder-surfing but not anyone with file access. The real boundary is filesystem permissions on `{ConfigDir}` — treat the config dir, backups, and any copy of `mangarr.db` as credential material.
- **Rate limits are shared state.** A 429/503 from any source becomes a `RateLimitException` (`RateLimitDetectingHandler` on the source clients; `RateLimitDetector` recognises it anywhere in an exception chain), and both the downloader (`ChapterDownloadProcessor`) and chapter sync (`ChapterSyncService`) feed it into the one `DownloadQueueService` cooldown. Exception: the `challenge-fetcher` client only converts 429 — **Cloudflare serves its challenge as 503**, and `ChallengeAwareFetcher` must see that status to hand off to FlareSolverr. Retries (`TransientRetryHandler`) deliberately skip 429/503 for the same reason, and only cover GET/HEAD so a qBittorrent add or Kavita scan is never replayed.
- **Adding a source** = one `ISource` impl in `Mangarr.Sources` + one `AddSingleton<ISource, ...>()` in `Program.cs`. Return chapters via `SourceChapterList.Normalize` — it dedupes by `(Number, Volume, Language)` and orders ascending; pass a `preferred` picker if the source lists the same chapter twice (scanlation groups, official vs rip).
