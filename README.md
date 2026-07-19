# Maki

A manga collection manager in the spirit of [Sonarr](https://sonarr.tv)/[Radarr](https://radarr.video):
add a series once, and Maki keeps it complete — monitoring sites for new chapters,
downloading pages, and packaging everything as **CBZ files with ComicInfo.xml** that
[Kavita](https://www.kavitareader.com) parses natively.

## Features

- **Metadata from [MangaBaka](https://mangabaka.org)** — one search identifies a series and
  brings along its MyAnimeList / AniList / MangaUpdates / Kitsu cross-IDs. Maki keeps a
  local copy of the [MangaBaka database](https://mangabaka.org/data/database) (nightly
  snapshot, ~3 GB on disk) so metadata search and library imports are instant and free of
  API rate limits; MangaBaka-original data is licensed
  [CC BY-NC-SA 4.0](https://creativecommons.org/licenses/by-nc-sa/4.0/).
- **Built-in site sources** (Suwayomi/Tachiyomi-style, compiled in):
  - **MangaDex** (official API)
  - **MangaPill**
  - **Weeb Central**
  - **MangaFire** (requires [FlareSolverr](https://github.com/FlareSolverr/FlareSolverr))
- **Automatic source matching** when you add a series, with manual linking for anything fuzzy.
- **Monitoring engine** — refreshes chapter lists every 30 minutes and auto-downloads new chapters.
- **Kavita-friendly output** — `{Series}/{Series} Vol.X Ch.Y.cbz` naming, ComicInfo.xml with
  series/number/volume/authors/genres/language/reading-direction, atomic imports (no torn files).
- **Library at a glance** — poster grid with per-series download state (Downloading / Queued /
  Complete / Missing), monitor status on every card, and a stats strip (series, monitored, on
  disk, missing, in queue).
- **Live activity queue** over SignalR, with retry/remove and per-page progress.
- **Torrent acquisition** — search releases via Prowlarr, grab to qBittorrent, auto-import on
  completion. Runs alongside direct scraper downloads in the same queue.
- **Scrobbling** — pushes read progress to **AniList**, **MyAnimeList** and **MangaBaka**, driven
  by reading progress read back from Kavita.
- **Discover** — local ONNX embeddings over the MangaBaka dump surface titles that match your
  library's *feel*, not just shared genre labels. Seed from specific titles and filter by year,
  rating, type, status, **genre**, **chapter count** and an obscurity dial. Each detail card
  shows categorized tags, per-source ratings and a few MyAnimeList reviews.
- **Themes** — pick an accent (Indigo / Rose / Emerald / Amber) or a light theme under Settings.
- REST API (`/api/v1`, `X-Api-Key` auth) + Swagger at `/swagger`.

## Quick start (Docker)

```yaml
services:
  maki:
    image: ghcr.io/orbitmpgh/maki:latest
    container_name: maki
    environment:
      - PUID=1000
      - PGID=1000
    volumes:
      - ./maki-config:/config
      - /path/to/manga-library:/library
    ports:
      - "8990:8990"
    restart: unless-stopped

  kavita:
    image: jvmilazz0/kavita:latest
    container_name: kavita
    volumes:
      - ./kavita-config:/kavita/config
      - /path/to/manga-library:/library   # same library!
    ports:
      - "5000:5000"
    restart: unless-stopped

  # Optional — only needed for Cloudflare-protected sources (MangaFire)
  flaresolverr:
    image: ghcr.io/flaresolverr/flaresolverr:latest
    container_name: flaresolverr
    ports:
      - "8191:8191"
    restart: unless-stopped
```

1. Open `http://localhost:8990`, go to **Settings** and add `/library` as a root folder.
2. (Optional) Set the FlareSolverr URL to `http://flaresolverr:8191` and hit **Test**.
3. **Add Series** → search → pick → Maki auto-links sources and syncs chapters.
4. Click the download button on a chapter (or **Search all missing**) and watch **Activity**.
5. Point a Kavita library at the same folder — the CBZs parse with full metadata.

The API key is generated on first run into `/config/config.json` and shown in Settings.

### Settings you'll want to visit

- **Root folders** — where CBZs are written (point Kavita at the same paths).
- **Metadata** — download the local MangaBaka dump (~3 GB) for instant, rate-limit-free search.
- **Discover index** — build the ONNX embedding index that powers recommendations.
- **Prowlarr / qBittorrent** — optional torrent acquisition.
- **Kavita** — optional scan triggers, cover/metadata push, and reading-progress scrobbling.
- **Scrobbling** — connect AniList / MyAnimeList / MangaBaka.
- **Appearance** — accent colour and light/dark theme.
- **Backup & Restore** — snapshot your database + `config.json` to a zip (see below).

## Backup & restore

Settings → **Backup & Restore** manages zip backups of your library. Each backup holds a
consistent snapshot of the database plus `config.json` — everything that isn't cheap to
regenerate. The MangaBaka dump, embeddings, covers and cache are deliberately excluded, so
backups stay small. Backups live under `{ConfigDir}/backups`; keep the newest N per kind with
the retention setting.

Maki also takes an automatic backup **immediately before any upgrade applies a database
migration** — migrations are forward-only, so this is your recovery path if an upgrade goes
wrong.

> **Backups contain your settings secrets (API keys, passwords) in plain text.** Treat a
> downloaded backup like a password.

**Restoring** replaces the current database and settings, then restarts Maki to apply. Under
Docker (`restart: unless-stopped`) or systemd the app comes back automatically; a bare
`dotnet run`/exe just exits and you start it again. You can also upload a backup zip from another
machine — Maki refuses one that's newer than the running version (its schema can't be
downgraded).

## Building the Docker image

The repository ships a multi-stage [`Dockerfile`](Dockerfile) that builds the frontend
(Node 22) and backend (.NET 10 SDK) and packages them into an `aspnet:10.0` runtime image with
the built SPA served from `wwwroot/`. Build and run it yourself:

```bash
# Build (tag however you like)
docker build -t maki:local .

# Run
docker run -d --name maki \
  -p 8990:8990 \
  -v "$PWD/maki-config:/config" \
  -v "/path/to/manga-library:/library" \
  maki:local
```

Multi-arch build & push to a registry with Buildx:

```bash
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t ghcr.io/<you>/maki:latest \
  --push .
```

Notes:
- The build context is the repo root; `.dockerignore` keeps `bin/`, `obj/`, `node_modules/`,
  `dist/` and dev config out of the context.
- `entrypoint.sh` drops privileges to `PUID`/`PGID` (via `gosu`) after fixing ownership of
  `/config`, so files land with your user's ownership.
- State persists in the `/config` volume; the library is a separate mount you share with Kavita.
- An identical Dockerfile lives at `distribution/docker/Dockerfile` for CI.

## Development

```bash
# Backend (http://localhost:8990, Swagger at /swagger)
dotnet run --project src/Maki.Api

# Frontend dev server (http://localhost:5173, proxies /api + /signalr)
npm --prefix frontend run dev

# Tests
dotnet test

# Release build (what the container ships)
dotnet build -c Release
```

State lives in `MAKI_CONFIG_DIR` (defaults to `/config` in Docker, `%APPDATA%\Maki`
on Windows). SQLite database, logs, covers, page cache, and the MangaBaka dump all live there.
For local development, point it at a throwaway dir so you don't touch your real library/DB:

```bash
MAKI_CONFIG_DIR="$PWD/.devconfig" dotnet run --project src/Maki.Api
```

EF Core migrations apply automatically on startup — no manual step.

## Architecture

```
src/
├── Maki.Api/        ASP.NET Core host — REST /api/v1, SignalR, Quartz jobs, download workers
├── Maki.Core/       Domain: entities, ISource/IMetadataProvider, parser, naming, CBZ pipeline
├── Maki.Data/       EF Core + SQLite
├── Maki.Sources/    Site scrapers (MangaDex, MangaPill, WeebCentral, MangaFire)
└── Maki.Metadata/   MangaBaka provider + local dump + ONNX embeddings
frontend/               Vite + React + TypeScript + Mantine SPA
```

Sources implement a single `ISource` interface (search / series / chapters / pages) and are
registered in DI — adding a site is one class plus one registration. Page requests carry their
own headers (Referer, cookies) end-to-end so hotlink-protected CDNs work uniformly.

## Legal

Maki is a tool for organizing your library. Scraper sources access third-party websites —
you are responsible for complying with those sites' terms of service and your local laws.
Support the industry: buy official releases.
