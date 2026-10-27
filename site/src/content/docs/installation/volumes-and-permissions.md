---
title: Volumes, PUID and PGID
description: What the /config and /library mounts hold, how PUID and PGID pick the user Maki runs as, and how to fix permission errors.
---

Maki uses two mounts. `/config` holds Maki's own state. `/library` holds your manga. This page covers
what goes in each, and how `PUID` and `PGID` decide who owns the files Maki writes.

## /config

Maki keeps everything it needs to run in `/config`:

- the SQLite database
- `config.json`, which holds the port and settings, including API keys
- the data protection keys, in `dataprotection-keys`, which sign session cookies. Anyone with this folder can sign in as any user, so keep it private. Backups leave it out.
- logs
- covers and the page cache
- the local MangaBaka database, the embedding index and the precomputed recommendation data
- backups, under `backups`

Backups do not include the MangaBaka database, embeddings, covers or cache, so they stay small. They do
include your settings in plain text. Treat a backup, `config.json` and a copy of the database as
passwords. See [Backup and restore](/Maki/guides/running/backup-restore/).

Keep `/config` on a folder only Maki uses. The container changes the ownership of everything in it on
every start (see below), so a shared folder can end up with a new owner.

## /library

`/library` is where Maki downloads chapters and where your existing series live. You add it as a library
folder in the setup guide. See [First-run setup](/Maki/installation/first-run/).

Maki does not change the ownership of `/library`. The user set by `PUID` and `PGID` has to be able to
write there already.

## PUID and PGID

The container starts as root only long enough to prepare `/config`. Then it runs Maki as the user and
group you set in `PUID` and `PGID`:

1. It creates `/config` if it is missing.
2. It runs `chown -R` on `/config` to `PUID:PGID`.
3. It starts Maki as `PUID:PGID` with `HOME` set to `/config`.

Use numeric IDs. The entrypoint passes them straight to `gosu` and does not look up names. If you leave
them out, they default to `1000`.

Set them to the owner of your library folder. On a Linux host, find it with:

```bash
ls -ln /path/to/manga-library
```

The third column is the user ID and the fourth is the group ID. Use those values for `PUID` and `PGID`.

## Fixing permission errors

**The setup guide says "Maki can't reach this folder".** The folder is missing, or the mount failed.
Check the path on the host, then check that the container has the mount:

```bash
docker exec maki ls -ln /library
```

**Downloads fail, or Maki cannot write a chapter.** The user in the container cannot write to the
library folder. Either change `PUID` and `PGID` to match the folder's owner, or give that owner write
access:

```bash
sudo chown -R 1000:1000 /path/to/manga-library
```

Use the same numbers as your `PUID` and `PGID`.

**The container exits right away with a `chown` error.** The `/config` mount is read-only, or it sits
on a filesystem that rejects ownership changes. Maki stops on that error, so mount a writable folder.

On Unraid, the template uses `99` and `100` for `PUID` and `PGID`. See
[Unraid paths and appdata](/Maki/installation/unraid-paths/).
