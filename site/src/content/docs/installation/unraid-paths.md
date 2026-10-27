---
title: Unraid paths and appdata
description: Which host paths the Maki Unraid template maps, what lives in appdata, and how to change or move them.
---

The Maki Unraid template maps two host paths into the container. This page explains what each one holds
and how to change them. For installing the template, see [Install on Unraid](/Maki/installation/unraid/).

## The two mappings

| Host path | Container path | What it holds |
|---|---|---|
| `/mnt/user/appdata/maki` | `/config` | Database, `config.json` with the API key, logs, covers, page cache and backups |
| `/mnt/user/manga` | `/library` | Your manga library. Maki downloads CBZ files here. |

The container paths are fixed. In the Unraid template you can change the host side of each mapping to
any share or folder you want. Do not change the container side.

The template also sets `PUID` to `99` and `PGID` to `100`, which is Unraid's `nobody` user and `users`
group. Maki changes the ownership of everything in `/config` to that pair on every start, so the
appdata folder ends up owned by `nobody:users`. The library folder must be writable by that pair too.
See [Volumes, PUID and PGID](/Maki/installation/volumes-and-permissions/).

## Appdata

`/mnt/user/appdata/maki` is the folder to back up. The backups Maki writes from **Settings → System →
Backup & restore** go into its `backups` subfolder, so they are included in any backup of appdata.

Stop the container before you copy or move that folder. Maki keeps its SQLite database open while it
runs, and a copy taken during a write can be unusable. Copy the folder while the container is stopped, then
start it again.

## Changing the library path

If your manga is on a different share, edit the **Library** mapping in the template and point it at
that folder. Then add `/library` under **Settings → Library → Root folders**.

To read the same files in Kavita, point a Kavita library at the same host folder. Kavita must see the
files, so mount that folder into the Kavita container too.
