---
title: Mirror and domain overrides
description: Point a built-in source at a mirror or a new domain with MAKI_SOURCE_ environment variables, and see which sources support them.
---

Some sites change domains, rotate them after takedowns, or are blocked where you live. Several built-in
sources read an environment variable that replaces the address Maki uses for them. These overrides are
set on the server, not in the web UI.

## The variables

| Variable | Source | Built-in address |
|---|---|---|
| `MAKI_SOURCE_TCBSCANS_BASEURL` | TCB Scans | `https://tcbonepiecechapters.com` |
| `MAKI_SOURCE_RAWKUMA_BASEURL` | Rawkuma | `https://rawkuma.net` |
| `MAKI_SOURCE_MANHWAWEB_APIURL` | ManhwaWeb | The API host only. The site address has no override. |
| `MAKI_SOURCE_ANIMESAMA_BASEURL` | Anime-Sama | `https://anime-sama.to` |
| `MAKI_SOURCE_SHINIGAMI_BASEURL` | Shinigami | `https://11.shinigami.asia`, used for links and URL resolution |
| `MAKI_SOURCE_SHINIGAMI_APIURL` | Shinigami | `https://api.shngm.io`, the API host |
| `MAKI_SOURCE_MANHUAGUI_BASEURL` | Manhuagui | `https://www.manhuagui.com` |
| `MAKI_SOURCE_OLYMPUS_BASEURL` | Olympus Scanlation | `https://olympusxyz.com` |
| `MAKI_SOURCE_CUUTRUYEN_BASEURL` | Cứu Truyện | `https://cuutruyen.net` |
| `MAKI_SOURCE_TEAMX_BASEURL` | Team-X | `https://olympustaff.com` |
| `MAKI_SOURCE_MANGAWORLD_BASEURL` | MangaWorld | `https://www.mangaworld.mx` |
| `MAKI_SOURCE_MANGALIB_APIURL` | MangaLib | `https://api.cdnlibs.org`, the API host |

Shinigami is split in two because its website number and its API host rotate separately. Set each one
on its own.

Custom sources don't use these variables. Each custom source has its own address in its definition.

## Set them in compose

Add the variables to the `environment` list of the `maki` service, then recreate the container. Use the
mirror's address with the scheme and no path. A trailing slash is fine, because Maki strips it.

```yaml
services:
  maki:
    image: ghcr.io/orbitmpgh/maki:latest
    environment:
      - PUID=1000
      - PGID=1000
      - MAKI_SOURCE_TCBSCANS_BASEURL=https://tcb-mirror.example
```

Run `docker compose up -d maki`. Maki reads these values when it starts, so a plain restart is not enough
after you change them in the compose file, and `up -d` recreates the container with the new values.

## Check that it works

A mirror has to serve the same site. Maki reads each source with that site's own layout, so a mirror
that serves a different layout will fail. A failing source shows **Error** on its row on the series page,
and hovering over it shows the message.

:::caution
An override changes where Maki connects for that source, for search, chapter lists and page images alike.
Don't point a variable at a site you don't trust. Maki sends the same requests to it that it sends to the
built-in address.
:::

To go back to the built-in address, remove the line from the compose file and run `docker compose up -d maki`
again.
