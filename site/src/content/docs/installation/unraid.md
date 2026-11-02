---
title: Install on Unraid
description: Install Maki from the Apps tab on an Unraid server.
---

Maki has an Unraid template. Search for **Maki** in the Apps tab of your Unraid server and click
**Install**.

## Template defaults

| Setting | Default | Notes |
|---|---|---|
| WebUI port | `8990` | The web interface. |
| Config | `/mnt/user/appdata/maki` | Database, logs, covers, page cache and `config.json`. |
| Library | `/mnt/user/manga` | Your manga library root, mounted at `/library`. |
| `PUID` | `99` | Matches Unraid's `nobody` user, so file ownership lines up. |
| `PGID` | `100` | Matches Unraid's `users` group. |

The template also passes `--memory-reservation=512m`. See [Memory](/Maki/installation/docker-compose/#memory)
for why that is a soft limit and not a hard cap.

## After the container starts

1. Open the WebUI, create the administrator account and follow the setup guide.
2. Add `/library` as a library folder.
3. To read with Kavita, point a Kavita library at the same share.

Continue with [Add your first series](/Maki/guides/getting-started/add-your-first-series/).
