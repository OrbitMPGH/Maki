---
title: Unraid paths and appdata
description: Which host paths the Maki Unraid template maps, what lives in appdata, and how to change or move them.
---

To install the template, see [Install on Unraid](/Maki/installation/unraid/).

## The two mappings

| Host path | Container path | What it holds |
|---|---|---|
| `/mnt/user/appdata/maki` | `/config` | Database, `config.json` (settings, including API keys), logs, covers, page cache and backups |
| `/mnt/user/manga` | `/library` | Your manga library. Maki downloads CBZ files here. |

Change the host side of each mapping to any share or folder you want. Do not change the container
side.

The template sets `PUID` to `99` and `PGID` to `100`, Unraid's `nobody` user and `users` group. Maki
changes the owner of anything in `/config` that is not already `99:100` on every start, so appdata
ends up owned by `nobody:users`. The library folder must be writable by that pair too. See
[Volumes, PUID and PGID](/Maki/installation/volumes-and-permissions/).

## Appdata

`/mnt/user/appdata/maki` is the folder to back up. Backups Maki writes from **Settings → System →
Backup & restore** go into its `backups` subfolder, so any backup of appdata includes them.

Stop the container before you copy or move that folder. Maki keeps its SQLite database open while it
runs, and a copy taken during a write can be unusable.

## Changing the library path

If your manga is on a different share, point the **Library** mapping in the template at that folder,
then add `/library` under **Settings → Library → Root folders**.

To read the same files in Kavita, mount that folder into the Kavita container too and point a Kavita
library at it.
