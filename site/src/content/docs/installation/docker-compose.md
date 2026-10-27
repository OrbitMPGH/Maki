---
title: Install with Docker Compose
description: The supported way to run Maki. One container, two folders.
---

import { Tabs, TabItem } from '@astrojs/starlight/components';

Maki publishes an image for amd64 and arm64 at `ghcr.io/orbitmpgh/maki:latest`.

## Create the compose file

<Tabs>
  <TabItem label="docker-compose.yml">
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
```
  </TabItem>
  <TabItem label="docker run">
```bash
docker run -d --name maki \
  -e PUID=1000 -e PGID=1000 \
  -v ./maki-config:/config \
  -v /path/to/manga-library:/library \
  -p 8990:8990 \
  --memory-reservation 512m \
  --restart unless-stopped \
  ghcr.io/orbitmpgh/maki:latest
```
  </TabItem>
</Tabs>

`PUID` and `PGID` set the user the container runs as. They should own both folders.

## Open Maki

1. Run `docker compose up -d`, open `http://localhost:8990` and create the administrator account.
2. The setup guide opens next. Add `/library` as a library folder there. Maki downloads the local
   MangaBaka database in the background (about 350 MB, 3.5 GB on disk) and finishes opening once it
   is installed.
3. Continue with [Add your first series](/Maki/guides/getting-started/add-your-first-series/).

## Memory

`mem_reservation` is a soft limit: when the host runs short of memory, the kernel takes Maki's file
cache back first. Most dashboards count that cache as the container's memory, so without it Maki can
look much bigger than it is. Don't set a hard `mem_limit` below 1.5 GB yet, since a scrape and a
search running together can briefly need close to 1 GB.

## Optional companions

Kavita, FlareSolverr and Suwayomi are optional. This compose file runs all of them next to Maki.

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

- **FlareSolverr:** set its URL under **Settings → Downloads → FlareSolverr** to
  `http://flaresolverr:8191` and hit **Test**.
- **Suwayomi:** set `http://suwayomi:4567` under **Settings → Integrations → Suwayomi**, hit **Test**,
  then **Manage sources** to turn on the ones you want.

## Upgrading from a single-user Maki

The first page asks you to set a username and password. Your library, history and tracker
connections are already attached to that account.

## Next

Offline reading and push notifications need HTTPS. See
[Reverse proxy and HTTPS](/Maki/installation/reverse-proxy-https/).
