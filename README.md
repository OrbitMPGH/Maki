# Mangarr

A manga collection manager in the spirit of [Sonarr](https://sonarr.tv)/[Radarr](https://radarr.video):
add a series once, and Mangarr keeps it complete — monitoring sites for new chapters,
downloading pages, and packaging everything as **CBZ files with ComicInfo.xml** that
[Kavita](https://www.kavitareader.com) parses natively.

## Features

- **Metadata from [MangaBaka](https://mangabaka.org)** — one search identifies a series and
  brings along its MyAnimeList / AniList / MangaUpdates / Kitsu cross-IDs. Mangarr keeps a
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
- **Live activity queue** over SignalR, with retry/remove and per-page progress.
- **Torrent acquisition** — search releases via Prowlarr, grab to qBittorrent, auto-import on
  completion. Runs alongside direct scraper downloads in the same queue.
- **Kavita scrobbling** — reads reading progress back from Kavita and marks chapters/volumes read.
- **Recommendations** — local ONNX embeddings over the MangaBaka dump surface similar titles.
- REST API (`/api/v1`, `X-Api-Key` auth) + Swagger at `/swagger`.

## Quick start (Docker)

```yaml
services:
  mangarr:
    image: ghcr.io/<you>/mangarr:latest
    container_name: mangarr
    environment:
      - PUID=1000
      - PGID=1000
    volumes:
      - ./mangarr-config:/config
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
3. **Add Series** → search → pick → Mangarr auto-links sources and syncs chapters.
4. Click the download button on a chapter (or **Search all missing**) and watch **Activity**.
5. Point a Kavita library at the same folder — the CBZs parse with full metadata.

The API key is generated on first run into `/config/config.json` and shown in Settings.

## Development

```bash
# Backend (http://localhost:8990, Swagger at /swagger)
dotnet run --project src/Mangarr.Api

# Frontend dev server (http://localhost:5173, proxies /api + /signalr)
npm --prefix frontend run dev

# Tests
dotnet test
```

State lives in `MANGARR_CONFIG_DIR` (defaults to `/config` in Docker, `%APPDATA%\Mangarr`
on Windows). SQLite database, logs, covers, and page cache all live there.

## Architecture

```
src/
├── Mangarr.Api/        ASP.NET Core host — REST /api/v1, SignalR, Quartz jobs, download workers
├── Mangarr.Core/       Domain: entities, ISource/IMetadataProvider, parser, naming, CBZ pipeline
├── Mangarr.Data/       EF Core + SQLite
├── Mangarr.Sources/    Site scrapers (MangaDex, MangaPill, WeebCentral, MangaFire)
└── Mangarr.Metadata/   MangaBaka provider
frontend/               Vite + React + TypeScript + Mantine SPA
```

Sources implement a single `ISource` interface (search / series / chapters / pages) and are
registered in DI — adding a site is one class plus one registration. Page requests carry their
own headers (Referer, cookies) end-to-end so hotlink-protected CDNs work uniformly.

## Legal

Mangarr is a tool for organizing your library. Scraper sources access third-party websites —
you are responsible for complying with those sites' terms of service and your local laws.
Support the industry: buy official releases.
