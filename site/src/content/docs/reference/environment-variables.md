---
title: Environment variables
description: Every MAKI_* variable Maki reads, plus PUID and PGID from the container, with defaults.
---

This page lists the environment variables Maki reads. Most installs need none of them. Set a variable in the compose file's `environment` list or with `docker run -e`, then restart Maki.

Values that are not a whole number, or that are negative, fall back to the default in the timer and cache variables below.

## Container

| Variable | Default | What it does |
|---|---|---|
| `PUID` | `1000` | User ID Maki runs as. The container changes the owner of `/config` to this user on each start. |
| `PGID` | `1000` | Group ID Maki runs as. Used with `PUID` for the same purpose. |
| `MAKI_RUNTIME` | `docker` in the image | Marks the install as running in Docker. The update check reports this as `IsDocker`. |
| `MAKI_CONFIG_DIR` | `/config` in the image | Folder for the database, logs, covers, caches and `config.json`. Unset, Maki uses `/config` if that folder exists, otherwise the per-user app data folder (`%APPDATA%\Maki` on Windows). See [Ports, paths and files on disk](/Maki/reference/ports-and-paths/). |

## Sign-in

| Variable | Default | What it does |
|---|---|---|
| `MAKI_ALLOW_LOCAL_LOGIN` | Off | Set to `1` or `true` to allow password sign-in for everyone when `auth.oidconly` is on. Admins can always sign in with a password. Use it to get back in when the identity provider is down. |

## Startup and memory

| Variable | Default | What it does |
|---|---|---|
| `MAKI_DUMP_BOOT_DOWNLOAD` | On | When no MangaBaka database is installed, the download starts about five seconds after boot. `0` delays it to two minutes after boot. |
| `MAKI_DROP_SCAN_CACHE` | On | After a scan reads a file end to end, the operating system drops that file's cached pages. `0` keeps them. Set `0` on a machine with RAM to spare. |
| `MAKI_EMBED_IDLE_MINUTES` | `5` | Minutes idle before the embedding model is unloaded from memory. `0` keeps it loaded. |
| `MAKI_ARTIFACT_IDLE_MINUTES` | `10` | Minutes idle before the in-memory catalogue and recommendation indexes are dropped. `0` keeps them loaded. |
| `MAKI_VECTOR_IDLE_MINUTES` | `15` | Minutes idle before the search vectors are dropped from memory. `0` keeps them loaded. |
| `MAKI_BROWSER_IDLE_MINUTES` | `3` | Minutes idle before the headless browser used by some sources shuts down. `0` keeps it running. |

## Embeddings

| Variable | Default | What it does |
|---|---|---|
| `MAKI_EMBED_PROVIDER` | `cpu` | `cuda` runs the embedding model on an NVIDIA GPU. Any other value runs it on the CPU. |
| `MAKI_EMBED_PRECISION` | `int8` on CPU, `fp32` on CUDA | Model precision: `int8` (or `quantized`) or `fp32` (or `float32`). |
| `MAKI_EMBED_BATCH` | `32` on CPU, `128` on CUDA | Items per batch when computing embeddings. |
| `MAKI_EMBED_MODEL_URL` | The Snowflake Arctic Embed M model on Hugging Face, for the chosen precision | URL the model file is downloaded from. |
| `MAKI_EMBED_VOCAB_URL` | The `vocab.txt` of the same model on Hugging Face | URL the tokenizer vocabulary is downloaded from. |

## Source domains

These override the built-in domain or API host of one source. Each row is one variable. For how to find a working mirror and set one, see [Mirror overrides](/Maki/guides/sources/mirror-overrides/).

| Variable | Default | What it does |
|---|---|---|
| `MAKI_SOURCE_TCBSCANS_BASEURL` | Built-in domain | Domain for TCB Scans. |
| `MAKI_SOURCE_RAWKUMA_BASEURL` | Built-in domain | Domain for Rawkuma. |
| `MAKI_SOURCE_MANHWAWEB_APIURL` | Built-in API host | API host for ManhwaWeb. |
| `MAKI_SOURCE_ANIMESAMA_BASEURL` | Built-in domain | Domain for Anime-Sama. |
| `MAKI_SOURCE_SHINIGAMI_APIURL` | Built-in API host | API host for Shinigami. |
| `MAKI_SOURCE_SHINIGAMI_BASEURL` | Built-in domain | Site domain for Shinigami. |
| `MAKI_SOURCE_MANHUAGUI_BASEURL` | Built-in domain | Domain for Manhuagui. |
| `MAKI_SOURCE_OLYMPUS_BASEURL` | Built-in domain | Domain for Olympus Scanlation. |
| `MAKI_SOURCE_CUUTRUYEN_BASEURL` | Built-in domain | Domain for Cứu Truyện. |
| `MAKI_SOURCE_TEAMX_BASEURL` | Built-in domain | Domain for Team-X. |
| `MAKI_SOURCE_MANGAWORLD_BASEURL` | Built-in domain | Domain for MangaWorld. |
| `MAKI_SOURCE_MANGALIB_APIURL` | Built-in API host | API host for MangaLib. |

## Tracker endpoints

These point each tracker at a different API or OAuth address. The code comment says they exist so end-to-end tests can point at mocks. Leave them unset for normal use.

| Variable | Default | What it does |
|---|---|---|
| `MAKI_SCROBBLE_ANILIST_API` | `https://graphql.anilist.co` | AniList GraphQL endpoint. |
| `MAKI_SCROBBLE_ANILIST_OAUTH` | `https://anilist.co/api/v2/oauth` | AniList OAuth endpoint. |
| `MAKI_SCROBBLE_MAL_API` | `https://api.myanimelist.net/v2` | MyAnimeList API. |
| `MAKI_SCROBBLE_MAL_OAUTH` | `https://myanimelist.net/v1/oauth2` | MyAnimeList OAuth endpoint. |
| `MAKI_SCROBBLE_KITSU_API` | `https://kitsu.app/api/edge` | Kitsu API. |
| `MAKI_SCROBBLE_KITSU_OAUTH` | `https://kitsu.app/api/oauth` | Kitsu OAuth endpoint. |
| `MAKI_SCROBBLE_MANGABAKA_API` | `https://api.mangabaka.org` | MangaBaka API. |
| `MAKI_SCROBBLE_MANGAUPDATES_API` | `https://api.mangaupdates.com/v1` | MangaUpdates API. |
