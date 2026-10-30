---
title: Install and upgrade
description: PUID and PGID, disk space, using an existing Kavita library, upgrades and what a backup protects.
---

### What do PUID and PGID do?

They set the user and group the container runs as. Maki chowns `/config` to that user at startup but not your library folder, so the same user needs read and write access there. See [Install with Docker Compose](/Maki/installation/docker-compose/).

### How much disk space does Maki need?

The local MangaBaka database (`mangabaka.db`, which Discover and metadata lookups read) is about 350 MB to download and 3.5 GB on disk. Plan for that on the drive holding `/config`, on top of your library. See
[Install with Docker Compose](/Maki/installation/docker-compose/#open-maki).

### Can I use my existing Kavita library?

Yes. Maki writes plain CBZ files with ComicInfo.xml, so Kavita reads the same files. Add the folder to Maki under **Settings → Library → Root folders**. Optional read sync is under **Settings → Reading → Kavita read sync**.

### How do I upgrade?

Docker and Unraid updates are manual. The Windows app checks for and installs updates itself, and the Linux tarball does not update itself. **Settings → System → Updates** shows the steps for your install. With Docker Compose: `docker compose pull`, then `docker compose up -d`.

### Does an upgrade change my data?

Migrations only move forward. Maki takes a backup before any upgrade that applies one, and that backup is your way back. See [Backup and restore](/Maki/guides/running/backup-restore/).

### I am upgrading from a single-user Maki. What happens to my account?

The first page asks you to set a username and password. Your library, history and tracker connections are already attached to it. See [Upgrading](/Maki/installation/upgrading/#upgrading-from-a-single-user-maki).

### Can I run Maki without Docker?

Yes. Maki has a Windows installer and Linux tarballs for x64 and arm64, both on the [releases page](https://github.com/OrbitMPGH/Maki/releases). The Windows app runs from the tray and keeps its data in `%LOCALAPPDATA%\Maki`. You can also build from source, which needs .NET 10 and Node 24, see the [development guide](/Maki/contribution/development/). On Unraid, install from the Apps tab, see [Unraid](/Maki/installation/unraid/).
