---
title: Ports, paths and files on disk
description: The port Maki listens on, what is in the config folder, where trash goes, and which URLs Maki serves.
---

Paths are shown as they appear inside the container.

## Port

Maki listens on `8990`. The port is the `port` value in `config.json`, read at startup.

If you change it, update the container side of the `ports` mapping in your compose file too. The health check reads `port` from `config.json`, so it follows.

## The config folder

The config folder is `/config` in the container, set by `MAKI_CONFIG_DIR`. Outside Docker, the fallback order is in [Environment variables](/Maki/reference/environment-variables/). Maki creates most of these on first start.

| Path | What it holds |
|---|---|
| `config.json` | Written on first start. Port, log level, HTTP request logging, slow-request threshold, log file size and count. No credentials. |
| `maki.db` | The main SQLite database: the library, accounts and settings. |
| `mangabaka.db` | The local MangaBaka metadata database. Maki downloads it in the background. |
| `embeddings.db` | The embeddings database used by semantic search. |
| `reco-edges.db`, `coread-edges.db`, `taste-vectors.db`, `reader-cohorts.db` | Precomputed recommendation data. Optional: a missing file leaves its signal empty. |
| `models/` | Downloaded embedding model files. |
| `logs/` | Rolling log files. `config.json` sets the size cap per file (20 MB) and files kept (7). |
| `cache/` | Downloads, reader thumbnails, source and conversion previews. Regenerated as needed and cleaned automatically. |
| `MediaCover/` | Cover images for the library. |
| `backups/` | Backup archives made from **Settings → System → Backup & restore**. |
| `restore-pending/` | A restore staged for the next startup. Normally empty. |
| `dataprotection-keys/` | Keys that sign session cookies and antiforgery tokens. Anyone holding them can sign in as any user. Backups never include this folder. Keep it private. |

## The library mount

Maki has no fixed library path. The examples mount a host folder as `/path/to/manga-library:/library`. Add `/library` as a root folder during setup or under **Settings → Library → Root folders**.

## `.maki-trash`

Maki moves removed files into a `.maki-trash` folder inside the root folder they came from, so trash counts against that volume's space.

| Path | What it holds |
|---|---|
| `<root>/.maki-trash/bin/<series id>/` | Files you deleted. They can be restored from the recycle bin. |
| `<root>/.maki-trash/<series id>/` | Files replaced by an upgrade. |
| `<root>/.maki-trash/<series id>/conv-*` | Originals replaced by page conversion. |

Retention is set in Settings and defaults to 14 days for both kinds. `0` purges on the next housekeeping run.

## URLs

| Path | What it is |
|---|---|
| `/` | The web app. |
| `/api/v1` | The REST API. See [API keys and the OpenAPI spec](/Maki/reference/api/). |
| `/api/v1/opds/{token}` | OPDS feed for reading apps. Token from **Settings → Reading → OPDS catalogue**. |
| `/komga` | Komga-compatible API. Komga-only apps use `http://host:8990/komga` as their address. |
| `/signalr/events` | Live-update connection for the web app. |
| `/initialize.json` | Startup info for the web app. The health check requests it. |
| `/swagger` | Swagger UI. Development environment only. |
