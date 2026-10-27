---
title: Install and upgrade
description: PUID and PGID, disk space, using an existing Kavita library, upgrades and what a backup protects.
---

### What do PUID and PGID do?

They set the user and group the container runs as. At startup, Maki changes the owner of everything
under `/config` to that user. It does not change your library folder, so the same user must be able
to read and write there too. See [Install with Docker Compose](/Maki/installation/docker-compose/).

### How much disk space does Maki need?

The local MangaBaka database is about 350 MB to download and 3.5 GB on disk. Maki stores it in
`/config` as `mangabaka.db`, and it is what Discover and metadata lookups read from. Plan for that
space on the drive that holds `/config`, on top of your library. See
[Install with Docker Compose](/Maki/installation/docker-compose/#open-maki).

### Can I use my existing Kavita library?

Yes. Maki writes plain CBZ files with ComicInfo.xml, so Kavita reads the same files. Point Kavita and
Maki at one folder, or add that folder to Maki as a root folder under **Settings → Library → Root
folders**. Kavita read sync is optional and lives under **Settings → Reading → Kavita read sync**.

### How do I upgrade?

Updating is manual. **Settings → System → Updates** says how for your install: pull the new image and
recreate the container. With Docker Compose that is `docker compose pull` followed by
`docker compose up -d`.

### Does an upgrade change my data?

Yes, in one way that matters for recovery. Maki takes a backup before any upgrade that applies a
database migration, and migrations only move forward. That backup is your way back if an upgrade goes
wrong. Restores are covered in [Backup and restore](/Maki/guides/running/backup-restore/).

### I am upgrading from a single-user Maki. What happens to my account?

The first page asks you to set a username and password. Your library, history and tracker connections
are already attached to that account, so nothing needs to be moved by hand. See
[Install with Docker Compose](/Maki/installation/upgrading/#upgrading-from-a-single-user-maki).

### Can I run Maki without Docker?

Yes. Building from source needs .NET 10 and Node 24, and the
[development guide](/Maki/contribution/development/) covers it. On Windows, Maki keeps its state in
`%APPDATA%\Maki`. Unraid users can install from the Apps tab, see [Unraid](/Maki/installation/unraid/).
