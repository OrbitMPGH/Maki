# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

Two personas, no priority between them:
- **Self-hosting hobbyist.** Already runs Sonarr/Radarr/Kavita/Docker/Unraid-style stacks. Comfortable with root folders, API keys, YAML compose files, source/tracker configuration.
- **Casual manga reader.** Wants a manga library and reader that works; doesn't care about self-hosting mechanics or how sources/scrobbling are wired underneath.

## Product Purpose

Maki is a manga collection manager in the Sonarr/Radarr mold: add a series once, and Maki keeps it complete going forward. It monitors source sites for new chapters, downloads pages, and packages everything as CBZ + ComicInfo.xml that Kavita (or any CBZ/OPDS reader) parses natively. Success is a library that stays current without manual re-checking, plus a usable in-app reader and discovery surface for people who don't want a second app.

## Positioning

Kavita-first but not Kavita-only: Maki owns acquisition (source monitoring, scraping, torrent fallback via Prowlarr/qBittorrent) and metadata (local MangaBaka mirror, no rate limits), and writes plain well-tagged CBZs any reader can consume — no proprietary lock-in. A neighboring "just a reader" app can't truthfully claim the automated, multi-source acquisition pipeline or the free rate-limit-free local metadata mirror.

## Operating Context

- Deployed via Docker alongside Kavita, sharing a library volume; also runs bare via `dotnet run` on Windows/Linux.
- Backend ASP.NET Core host (`:8990`) + Vite/React/Mantine SPA (`:5173` in dev, proxied).
- Core workflows: Add Series (search MangaBaka → auto-link sources) → Monitoring (scheduled chapter-list refresh) → Download (scraper queue or torrent via Prowlarr/qBittorrent) → Library (poster grid, per-series state) → Reader (built-in CBZ reader) → Discover (embedding-based recommendations) → Rewind (reading/download stats) → Scrobble (AniList/MyAnimeList/MangaBaka progress push) → Settings (root folders, metadata, sources, backup/restore, appearance).
- Real-time activity queue over SignalR (download progress, retry/remove).
- REST API at `/api/v1` with `X-Api-Key` auth, Swagger docs.

## Capabilities and Constraints

- Built-in scraper sources: MangaDex, MangaPill, WeebCentral, MangaFire (needs FlareSolverr), MangaPlus, TCB Scans, Asura, WEBTOON, Flame Comics.
- Local MangaBaka metadata mirror (~3 GB nightly snapshot) for instant, rate-limit-free search and cross-IDs (MAL/AniList/MangaUpdates/Kitsu).
- Torrent acquisition alongside direct scraping, same download queue.
- Scrobbling to AniList, MyAnimeList, MangaBaka.
- Discover: local ONNX embeddings over the MangaBaka dump for feel-based recommendations, filterable by year/rating/type/status/genre/chapter count/obscurity.
- Rewind: append-only reading/download history.
- Backup & restore: zip snapshot of DB + config, auto-backup before schema migrations.
- Pre-1.0, schema/API can still shift between releases.
- Existing accent themes: Indigo / Rose / Emerald / Amber, plus a light theme — open for extension, not required to preserve exactly.

## Brand Commitments

- Product name is **Maki** — fixed.
- Existing favicon (`frontend/public/favicon.svg`) is the current fixed mark; no other visual identity (palette, type, logo lockup) is locked yet.

## Evidence on Hand

- Screenshots under `docs/` (library, discover, import, add-series, series, scrobble, genre, recommendations, rewind) show the current shipped UI — treat as incumbent visual truth, not aspirational.
- No testimonials, case studies, press, or pricing exist; none should be fabricated.

## Product Principles

1. **Own your files.** Every output is a standard CBZ + ComicInfo.xml on disk — no proprietary database lock-in, ever.
2. **Set-and-forget by default.** Once a series is added, staying current requires no further user action; UI should reflect state, not demand babysitting.
3. **Kavita-first, reader-agnostic in practice.** Optimize integration depth with Kavita, but never assume Kavita is the only consumer of the library.
4. **Free and rate-limit-free where possible.** The local MangaBaka mirror exists specifically to avoid API throttling — design should not reintroduce friction that mirror was built to remove.
5. **Serve both personas without forking the UI.** Power-user configuration (sources, torrents, scrobble targets) and casual daily use (browse, read, discover) coexist in one app; don't bury casual flows under hobbyist complexity or vice versa.

## Accessibility & Inclusion

No product-specific accessibility requirement has been established yet.
