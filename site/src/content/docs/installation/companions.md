---
title: FlareSolverr and Suwayomi
description: The optional Kavita, FlareSolverr and Suwayomi containers, and where each one is set up in Maki.
---

Kavita, FlareSolverr and Suwayomi are optional. Maki works without any of them. This page shows how
to run them next to Maki in one compose file, and where each one is connected in Maki's settings.

## Compose file

This file runs Maki and all three companions on one network. Maki reaches each companion by its
service name, such as `http://flaresolverr:8191`.

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
    mem_reservation: 512m
    restart: unless-stopped

  # Point a Kavita library at the same folder.
  kavita:
    image: jvmilazz0/kavita:latest
    container_name: kavita
    volumes:
      - ./kavita-config:/kavita/config
      - /path/to/manga-library:/library
    ports:
      - "5000:5000"
    restart: unless-stopped

  # Needed for Cloudflare-protected sources.
  flaresolverr:
    image: ghcr.io/flaresolverr/flaresolverr:latest
    container_name: flaresolverr
    ports:
      - "8191:8191"
    restart: unless-stopped

  # Extension sources. Install extensions in its own UI.
  suwayomi:
    image: ghcr.io/suwayomi/suwayomi-server:stable
    container_name: suwayomi
    volumes:
      - ./suwayomi-data:/home/suwayomi/.local/share/Tachidesk
    ports:
      - "4567:4567"
    restart: unless-stopped
```

The `ports` lines only matter if you want to open a companion's own web interface from your browser.
Maki does not need them.

## Kavita

[Kavita](https://www.kavitareader.com) is a reader for your library folder. Maki writes its CBZ files
with ComicInfo.xml, so Kavita can read the same files without any conversion.

Kavita needs no link to Maki to read the files. To connect the two, fill in the Kavita card under
**Settings → Integrations → Kavita**:

- **URL** is the address Maki uses to reach Kavita. In the compose file above, that is
  `http://kavita:5000`.
- **API key** comes from Kavita under User Settings, 3rd Party Clients.
- **Path mapping** is only needed when Maki and Kavita see the library folder at different paths. The
  compose file mounts `/path/to/manga-library` at `/library` in both containers, so you can leave
  both mapping fields empty.

The same card also has **Attribute Kavita's reading to**, which picks the Kavita user that reading
progress is attributed to.
See [Reading over OPDS and Komga](/Maki/guides/reading/opds-apps/) if you read in Kavita or another
app instead.

## FlareSolverr

FlareSolverr solves Cloudflare challenges for sources that sit behind them. Maki sends those requests
to FlareSolverr, which returns the page.

1. Open **Settings → Downloads → FlareSolverr**.
2. Set the URL to `http://flaresolverr:8191`.
3. Click **Test**.

The [built-in sources list](/Maki/guides/sources/built-in-sources/) marks the sources that require it.
The rest work without it.

## Suwayomi

Suwayomi is a server for Tachiyomi-style extensions. Maki uses it to add more sources next to the
built-in ones. Extensions are installed in Suwayomi's own interface, not in Maki.

1. Open **Settings → Integrations → Suwayomi**.
2. Set the URL to `http://suwayomi:4567`.
3. Click **Test**.
4. Click **Manage sources** and turn on the ones you want Maki to use.

The `suwayomi-data` folder in the compose file keeps Suwayomi's data between restarts.
