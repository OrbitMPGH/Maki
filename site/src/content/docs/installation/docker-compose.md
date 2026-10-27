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
  <TabItem label="Synology / Portainer">
Paste the compose file above into a Portainer stack, or into a Synology Container Manager project. Map
`/config` and `/library` to shared folders that the user set in `PUID` and `PGID` can write to.
  </TabItem>
</Tabs>

`PUID` and `PGID` set the user the container runs as. Maki changes the ownership of `/config` to that user on
every start, but not `/library`, so that user needs write access to your library folder already. See
[Volumes, PUID and PGID](/Maki/installation/volumes-and-permissions/).

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

Kavita, FlareSolverr and Suwayomi are optional. The compose file for all three, and where each one is
connected in Maki, is on [FlareSolverr and Suwayomi](/Maki/installation/companions/).

## Upgrading

Upgrading from a single-user Maki, and going back after a bad upgrade, are covered on
[Upgrading](/Maki/installation/upgrading/).

## Next

Offline reading and push notifications need HTTPS. See
[Reverse proxy and HTTPS](/Maki/installation/reverse-proxy-https/).
