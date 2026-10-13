# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

Two personas, no priority between them:
- **Self-hosting hobbyist.** Already runs Sonarr/Radarr/Kavita/Docker/Unraid-style stacks. Comfortable with root folders, API keys, YAML compose files, source/tracker configuration.
- **Casual manga reader.** Wants a manga library and reader that works; doesn't care about self-hosting mechanics or how sources/scrobbling are wired underneath.

Multi-user is a capability, not a third persona: one instance can serve a household, and either persona may be on an account someone else administers.

## Product Purpose

Maki is a manga collection manager in the Sonarr/Radarr mold: add a series once, and Maki keeps it complete going forward. It monitors source sites for new chapters, downloads pages, and packages everything as CBZ + ComicInfo.xml that Kavita (or any CBZ/OPDS reader) parses natively. Success is a library that stays current without manual re-checking, plus a usable in-app reader and discovery surface for people who don't want a second app.

## Positioning

Kavita-first but not Kavita-only: Maki owns acquisition (built-in multi-language source monitoring, optional Suwayomi extension bridge, torrent fallback via Prowlarr/qBittorrent) and metadata (local MangaBaka mirror, no rate limits), and writes plain well-tagged CBZs any reader can consume, with no proprietary lock-in. Sources are built in, so there are no extensions to install. A neighboring "just a reader" app can't truthfully claim the automated, multi-source acquisition pipeline or the free, rate-limit-free local metadata mirror.

## Operating Context

- Deployed via Docker alongside Kavita, sharing a library volume; also runs bare via `dotnet run` or the Windows executable. FlareSolverr and Suwayomi are optional sidecars.
- Usually reached over plain HTTP on the LAN, which is not a secure context; exposing it to the internet behind a TLS reverse proxy is supported and documented.
- Backend ASP.NET Core host (`:8990`) + Vite/React/Mantine SPA (`:5173` in dev, proxied).
- Core workflows: Add Series (search MangaBaka, sources match in the background) → Monitoring (scheduled chapter-list refresh, monitor modes, per-chapter Wanted) → Download (scraper queue or torrent via Prowlarr/qBittorrent) → Library (grid/list, per-series state, saved filters, health checks, manual import) → Reader (built-in CBZ reader, reading profiles) → Home (per-user rails) → Discover (embedding-based recommendations, Browse, Your Taste) → Stats (reading/download history, achievements and levels; "Rewind" is its year playback) → Scrobble (AniList/MyAnimeList/Kitsu/MangaBaka progress push, tracker list import) → Settings (root folders, users, sources, metadata, notifications, security, backup/restore, appearance).
- `/` redirects to the user's chosen start page rather than being a page itself.
- Reading also happens outside the SPA: in Kavita, and over OPDS in Panels, Chunky, KOReader, or Mihon, with progress counted back into Maki.
- Real-time activity queue over SignalR (download progress, retry/remove, downloads that need a decision).
- REST API at `/api/v1` with per-user `X-Api-Key` auth, Swagger in Development.

## Capabilities and Constraints

- Around 37 built-in scraper sources across English, Japanese (official and raws), Korean, Chinese, Portuguese, Spanish, French, German, Italian, Russian, Turkish, Indonesian, Vietnamese, and Arabic, including official ones (MangaDex, MANGA Plus, WEBTOON, Naver Webtoon, GigaViewer sites, KadoComi). Several need FlareSolverr. README's Sources table is the authoritative list.
- Optional Suwayomi bridge: sources from extensions installed in Suwayomi appear next to the built-in ones, off until enabled.
- Multilingual libraries: a series' source link can hold several languages, each with its own chapters and files; per-user title-language preference picks the display title.
- Local MangaBaka metadata mirror (~3 GB nightly snapshot) for instant, rate-limit-free search and cross-IDs (MAL/AniList/MangaUpdates/Kitsu).
- Torrent acquisition alongside direct scraping, same download queue; non-CBZ downloads and imports are converted on the way in (PDF is kept and read in place).
- Scrobbling to AniList, MyAnimeList, Kitsu, MangaBaka, whether reading happened in Maki, Kavita, or over OPDS.
- OPDS 1.2 server with page streaming, off by default, per-user rotatable feed token.
- Discover: local ONNX embeddings over the MangaBaka dump for feel-based recommendations, filterable by year/rating/type/status/genre/chapter count/content rating/popularity; Browse rails and a Your Taste view.
- Stats: append-only reading/download history, plus a per-user progression layer (level, achievements, streaks, self-set goals) derived from the same log and hideable with one switch. Rewind is the animated year recap it launches. Per-series incognito keeps a title out of stats.
- Multi-user: per-user history, preferences, tracker accounts, reading profiles, content-rating ceiling and Home layout over one shared library; library access granted per root folder; permission flags per account. Two-factor auth, per-user API keys, OpenID Connect single sign-on, lockout, trusted proxies, HTTPS enforcement.
- Library health: index and verify archives, find missing, unreadable, duplicate, or suspicious files; destructive operations are reviewed before they run.
- Notifications to Discord, Telegram, Slack, Notifiarr, ntfy, Gotify, Pushover, Apprise, or a webhook, scopable by user tag.
- Backup & restore: zip snapshot of DB + config, auto-backup before schema migrations.
- Eleven UI languages (English source; others machine-translated, corrected through Weblate). All user-facing text goes through the i18n layer, and layouts must survive longer translated strings.
- Appearance is two per-user choices: a background (Tinted black default, Night, Charcoal, Moss, Light, Match system) and an accent (Indigo, Rose, Blush, Emerald, Amber). Open for extension, not required to preserve exactly.
- Pre-1.0, schema/API can still shift between releases.

## Brand Commitments

- Product name is **Maki**, fixed.
- Existing favicon (`frontend/public/favicon.svg`) is the current fixed mark; no other visual identity (palette, type, logo lockup) is locked yet.

## Evidence on Hand

- Screenshots under `docs/` (activity, add, discover, genre, import, library, recommendations, rewind, scrobble, series) show shipped UI from earlier releases. Treat them as incumbent visual truth, but the running app is newer.
- No testimonials, case studies, press, or pricing exist; none should be fabricated.

## Product Principles

1. **Own your files.** Every output is a standard CBZ + ComicInfo.xml on disk, with no proprietary database lock-in, ever.
2. **Set-and-forget by default.** Once a series is added, staying current requires no further user action; UI should reflect state, not demand babysitting.
3. **Kavita-first, reader-agnostic in practice.** Optimize integration depth with Kavita, but never assume Kavita is the only consumer of the library.
4. **Free and rate-limit-free where possible.** The local MangaBaka mirror exists specifically to avoid API throttling; design should not reintroduce friction that mirror was built to remove.
5. **Serve both personas without forking the UI.** Power-user configuration (sources, torrents, scrobble targets) and casual daily use (browse, read, discover) coexist in one app; don't bury casual flows under hobbyist complexity or vice versa.

## Accessibility & Inclusion

No formal accessibility standard has been set (confirmed 2026-10-13).
