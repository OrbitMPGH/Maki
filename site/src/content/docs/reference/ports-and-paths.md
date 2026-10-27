---
title: Ports, paths and files on disk
description: The port Maki listens on, what is in the config folder, where trash goes, and which URLs Maki serves.
---

This page lists where Maki listens, what it keeps in its config folder, where it puts removed files, and which URLs it serves. Paths are shown as they appear inside the container.

## Port

Maki listens on port `8990`. The port is the `port` value in `config.json`, and it is read at startup.

If you change it, update the container side of the `ports` mapping in your compose file too. The container's health check reads the `port` value from `config.json`, so it follows the change.

## The config folder

The config folder is `/config` in the container, set by `MAKI_CONFIG_DIR`. Outside Docker, see [Environment variables](/Maki/reference/environment-variables/) for the fallback order. Maki creates most of these folders on first start.

| Path | What it holds |
|---|---|
| `config.json` | Written on first start. The port, log level, HTTP request logging, the slow-request threshold, and the log file size and count. It holds no credentials. |
| `maki.db` | The main SQLite database: the library, accounts and settings. |
| `mangabaka.db` | The local MangaBaka metadata database. Maki downloads it in the background. |
| `embeddings.db` | The embeddings database used by semantic search. |
| `reco-edges.db`, `coread-edges.db`, `taste-vectors.db`, `reader-cohorts.db` | Precomputed data for recommendations. These are optional. If one is missing, the signal it feeds is simply empty. |
| `models/` | Downloaded embedding model files. |
| `logs/` | Rolling log files. `config.json` sets the size cap per file (20 MB by default) and how many rolled files to keep (7 by default). |
| `cache/` | Downloads, reader page thumbnails, source previews and conversion previews. Maki regenerates these as needed and clears old files on its own. |
| `MediaCover/` | Cover images for the library. |
| `backups/` | Backup archives made from **Settings → System → Backup & restore**. |
| `restore-pending/` | A restore staged to apply on the next startup. Normally empty. |
| `dataprotection-keys/` | Keys that sign session cookies and antiforgery tokens. Anyone who holds these can sign in as any user. Backups never include this folder. Keep it private. |

## The library mount

Maki has no fixed library path. The examples mount a host folder at `/library`, as in `/path/to/manga-library:/library`. Add that path as a library folder during setup, or later on **Settings → Library → Root folders**. Maki reads whatever paths you add.

## `.maki-trash`

Maki does not delete files outright. It moves them into a `.maki-trash` folder inside the root folder they came from. Trash counts against the space on that volume.

| Path | What it holds |
|---|---|
| `<root>/.maki-trash/bin/<series id>/` | Files you deleted. They can be restored from the recycle bin. |
| `<root>/.maki-trash/<series id>/` | Files replaced by an upgrade. |
| `<root>/.maki-trash/<series id>/conv-*` | Originals replaced by page conversion. |

Retention is set in Settings. Both kinds default to 14 days. A value of `0` purges the files on the next housekeeping run.

## URLs

| Path | What it is |
|---|---|
| `/` | The web app. |
| `/api/v1` | The REST API. See [API keys and the OpenAPI spec](/Maki/reference/api/). |
| `/api/v1/opds/{token}` | The OPDS feed for reading apps. The token comes from **Settings → Reading → OPDS catalogue**. |
| `/komga` | A Komga-compatible API. Apps that only speak Komga take `http://host:8990/komga` as their address and add `/api/v1/...` to it. |
| `/signalr/events` | The live-update connection the web app uses. |
| `/initialize.json` | Startup information for the web app. The container's health check requests it. |
| `/swagger` | Swagger UI. Served only in the Development environment. |
